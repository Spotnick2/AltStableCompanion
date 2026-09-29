using System.Globalization;
using System.Text;
using AltStableCompanion.Core;

namespace AltStableCompanion.Core.Tests;

/// <summary>
/// Everything a test needs, generated: no binary fixtures. Images are drawn in code, TGAs are
/// written in the exact shapes WoW produces, and a temporary WoW install is laid out on disk.
/// </summary>
internal static class TestData
{
    public static RgbaImage Solid(int w, int h, byte r, byte g, byte b, byte a = 255)
    {
        var img = new RgbaImage(w, h);
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
                img[x, y] = (r, g, b, a);
        return img;
    }

    /// <summary>
    /// A black/white pair of one "figure": an opaque red rectangle, plus an optional column of
    /// half-covered pixels whose true colour is pure red (so black reads (128,0,0) and white
    /// (255,128,128)).
    /// </summary>
    public static (RgbaImage Black, RgbaImage White) Pair(int w, int h, int fx, int fy, int fw, int fh,
        bool halfColumn = false)
    {
        var black = Solid(w, h, 0, 0, 0);
        var white = Solid(w, h, 255, 255, 255);
        for (var y = fy; y < fy + fh; y++)
        {
            for (var x = fx; x < fx + fw; x++)
            {
                black[x, y] = (255, 0, 0, 255);
                white[x, y] = (255, 0, 0, 255);
            }
            if (halfColumn)
            {
                black[fx + fw, y] = (128, 0, 0, 255);
                white[fx + fw, y] = (255, 128, 128, 255);
            }
        }
        return (black, white);
    }

    /// <summary>A TGA exactly as WoW writes a screenshot: RLE (type 10), 32 bpp, top-left origin, 0 alpha bits.</summary>
    public static byte[] WowScreenshot(RgbaImage img)
    {
        using var ms = new MemoryStream();
        var header = new byte[18];
        header[2] = 10;
        header[12] = (byte)img.Width; header[13] = (byte)(img.Width >> 8);
        header[14] = (byte)img.Height; header[15] = (byte)(img.Height >> 8);
        header[16] = 32;
        header[17] = 0x20;                                  // top-left, no alpha bits
        ms.Write(header);

        // A linear run-length encoding over the whole image - packets DO cross rows.
        var n = img.Width * img.Height;
        var i = 0;
        while (i < n)
        {
            var run = 1;
            while (i + run < n && run < 128 && Same(img, i, i + run)) run++;
            if (run > 1)
            {
                ms.WriteByte((byte)(0x80 | (run - 1)));
                WriteBgra(ms, img, i, padAlpha: true);
                i += run;
            }
            else
            {
                var start = i;
                var raw = 1;
                while (start + raw < n && raw < 128 && !(start + raw + 1 < n && Same(img, start + raw, start + raw + 1))) raw++;
                ms.WriteByte((byte)(raw - 1));
                for (var k = 0; k < raw; k++) WriteBgra(ms, img, start + k, padAlpha: true);
                i += raw;
            }
        }
        return ms.ToArray();
    }

    private static bool Same(RgbaImage img, int a, int b)
    {
        for (var c = 0; c < 3; c++)
            if (img.Pixels[a * 4 + c] != img.Pixels[b * 4 + c]) return false;
        return true;
    }

    private static void WriteBgra(Stream s, RgbaImage img, int index, bool padAlpha)
    {
        var o = index * 4;
        s.WriteByte(img.Pixels[o + 2]);
        s.WriteByte(img.Pixels[o + 1]);
        s.WriteByte(img.Pixels[o]);
        s.WriteByte(padAlpha ? (byte)0 : img.Pixels[o + 3]);   // WoW pads with 0
    }

    public static string Stamp(DateTime t) => t.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    public static string ShotName(DateTime t) =>
        "WoWScrnShot_" + t.ToString("MMddyy_HHmmss", CultureInfo.InvariantCulture) + ".tga";

    public static long Epoch(DateTime local) => new DateTimeOffset(local).ToUnixTimeSeconds();

    /// <summary>One render record, formatted the way WoW writes SavedVariables.</summary>
    public static string Record(string name, string guid, int shot, DateTime stamp, long? epoch = null,
        int screenH = 2160) => $$"""
        {
        ["stamp"] = "{{Stamp(stamp)}}",
        ["guid"] = "{{guid}}",
        ["class"] = "HUNTER",
        ["raceLoc"] = "Windshaper Skyborne",
        ["name"] = "{{name}}",
        ["level"] = 16,
        ["race"] = "Skyborne",
        ["shot"] = {{shot}},
        ["sex"] = 3,
        ["epoch"] = {{epoch ?? Epoch(stamp)}},
        ["screenH"] = {{screenH}},
        ["screenW"] = 3840,
        ["uiScale"] = 0.6399999856948853,
        }
        """;

    /// <summary>
    /// A whole AltStable.lua: the large, unrelated AltStableDB first - with a decoy
    /// ["renders"] key in the character data - then the capture store.
    /// </summary>
    public static string SavedVariables(IEnumerable<string> records, int version = 1)
    {
        var sb = new StringBuilder();
        sb.Append("""

            AltStableDB = {
            ["Player-1-0001"] = {
            ["name"] = "Some Alt",
            ["gear"] = {
            {
            ["id"] = 1,
            }, -- [1]
            },
            ["renders"] = {
            {
            ["name"] = "Decoy", ["guid"] = "gx", ["shot"] = 1, ["stamp"] = "2020-01-01 00:00:00",
            },
            },
            ["note"] = "braces { in } a string, and a \"quote\"",
            },
            }
            AltStableConfig = {
            ["rosterView"] = "scene",
            }
            AltStablePortraits = {
            ["facing"] = 0,

            """);
        sb.Append("[\"version\"] = ").Append(version).Append(",\n[\"renders\"] = {\n");
        var n = 1;
        foreach (var r in records) sb.Append(r).Append(", -- [").Append(n++).Append("]\n");
        sb.Append("},\n}\n");
        return sb.ToString();
    }
}

/// <summary>A temporary WoW flavour folder, removed afterwards.</summary>
internal sealed class TempInstall : IDisposable
{
    public TempInstall()
    {
        Root = Path.Combine(Path.GetTempPath(), "asc-test-" + Guid.NewGuid().ToString("N"));
        Install = new WowInstall(Path.Combine(Root, "_classic_beta_"));
        Directory.CreateDirectory(Install.Screenshots);
        Directory.CreateDirectory(Install.AddOnsDir);
        File.WriteAllText(Path.Combine(Install.FlavorDir, "WowB.exe"), "");
    }

    public string Root { get; }
    public WowInstall Install { get; }

    public void WriteStore(string account, string text)
    {
        var dir = Path.Combine(Install.AccountsDir, account, "SavedVariables");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, SavedVariablesReader.FileName), text);
    }

    public string WriteShot(DateTime when, RgbaImage img)
    {
        var path = Path.Combine(Install.Screenshots, TestData.ShotName(when));
        File.WriteAllBytes(path, TestData.WowScreenshot(img));
        return path;
    }

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); } catch (IOException) { }
    }
}
