namespace AltStableCompanion.Core;

/// <summary>
/// What the Roster needs to crop the texture back, plus the capture it came from. Written
/// beside the TGA as a JSON sidecar; the manifest is rebuilt from these.
/// </summary>
public sealed record CutoutMeta
{
    public int W { get; init; }
    public int H { get; init; }
    public int TexW { get; init; }
    public int TexH { get; init; }
    /// <summary>Fraction of SCREEN HEIGHT the figure's width occupies. Not a race height.</summary>
    public double? NativeW { get; init; }
    public double? NativeH { get; init; }
    public string? NativeUnit { get; init; }
    public int[]? NativePx { get; init; }
    /// <summary>Whose portrait this is: the manifest keys the entry by it.</summary>
    public string? Guid { get; init; }
    /// <summary>The capture's shot-1 epoch: "already converted" is guid + epoch.</summary>
    public long? Epoch { get; init; }

    /// <summary>
    /// The two screenshots it was made from, by file name. While they are on disk (the player
    /// keeps screenshots, or one could not be deleted) they are THIS capture's, and no other
    /// capture may be matched to them.
    /// </summary>
    public string[]? Shots { get; init; }

    /// <summary>
    /// Set on the sidecar of an enhanced texture (Cutouts\Enhanced\): the plain portrait it
    /// was made from, by the hash of that file's bytes, and what made it.
    /// </summary>
    public EnhancementMeta? Enhancement { get; init; }

    /// <summary>
    /// A standing character is much taller than it is wide. Nearly square means something else
    /// survived the matte - a tooltip above the stage is the one that has happened. Worked out
    /// from the native size the sidecar keeps, so EVERY pass can say it, not only the one that
    /// wrote the cutout (the next pass follows within seconds).
    /// </summary>
    public bool NearlySquare => NativePx is [var w, var h] && w > h * 0.8;
}

public sealed record Cutout(RgbaImage Canvas, CutoutMeta Meta)
{
    public bool NearlySquare => Meta.NearlySquare;
}

/// <summary>One black/white pair to one cutout. Ported from make-cutout.py's convert().</summary>
public static class CutoutConverter
{
    /// <summary>
    /// The tallest a cutout is made. An enhanced picture is not scaled at all below it: Codex
    /// returns 1536 rows, the figure takes about 1400 of them, and the Char page draws it
    /// about that tall (<see cref="CharPageRows"/>).
    /// </summary>
    public const int MaxHeight = 2048;

    /// <summary>
    /// How tall the Char page draws a figure: it fits it to its portrait box, whatever the
    /// capture. Measured 2026-11-07 at 3840x2160 (UI scale off): 1329 of <see cref="CharPageOf"/>
    /// screen rows, in a 1339-row box. The camp draws one about 920 tall. The first guess,
    /// 1050, was read off a pasted screenshot that had been scaled down.
    /// </summary>
    public const int CharPageRows = 1329;

    public const int CharPageOf = 2160;

    /// <summary>
    /// How tall a capture is made: as tall as the Char page draws it on the screen it was
    /// taken on, never taller than the capture. The capture is a little taller than that (1373
    /// of 2160 rows), so it is still scaled down a little - the downscale is what gives the
    /// edges their partial alpha (see <see cref="Resampler"/>). Halves round up, in integers.
    /// </summary>
    public static int HeightFor(int nativeHeight, int screenHeight)
    {
        var drawn = (screenHeight * CharPageRows + CharPageOf / 2) / CharPageOf;
        return Math.Clamp(Math.Min(nativeHeight, drawn), 1, MaxHeight);
    }

    /// <summary>A figure this much of the screen or more is the whole window, not a character.</summary>
    public const double MaxScreenFraction = 0.95;

    public static Cutout Convert(RgbaImage black, RgbaImage white, string? guid = null, long? epoch = null)
    {
        var cut = Matte.Compute(black, white);
        var nativeW = cut.Width;
        var nativeH = cut.Height;
        // The screenshot's own height is the yardstick - the image cannot be wrong about itself.
        var shotH = black.Height;

        // The render stage is a fixed size in UI units and lands around 0.6 of the frame.
        // Near 1.0 means the matte caught the whole window; and because heights are RELATIVE,
        // one of those filed draws every other character at half size.
        if (nativeH >= shotH * MaxScreenFraction)
        {
            throw new NotAPairException(
                $"the cutout is {nativeH} of {shotH} screen rows tall - the matte caught the whole window, not the character");
        }

        var (canvas, scaled) = OnCanvas(cut, HeightFor(nativeH, shotH));

        var meta = new CutoutMeta
        {
            W = scaled.Width,
            H = scaled.Height,
            TexW = canvas.Width,
            TexH = canvas.Height,
            NativeUnit = "screen",
            NativeW = Math.Round(nativeW / (double)shotH, 5, MidpointRounding.ToEven),
            NativeH = Math.Round(nativeH / (double)shotH, 5, MidpointRounding.ToEven),
            NativePx = [nativeW, nativeH],
            Guid = guid,
            Epoch = epoch,
        };

        return new Cutout(canvas, meta);
    }

    /// <summary>
    /// The figure at <paramref name="height"/> (never scaled up) on the power-of-two canvas
    /// the game loads, top-left: the one shape every cutout has, plain or enhanced.
    /// </summary>
    public static (RgbaImage Canvas, RgbaImage Scaled) OnCanvas(RgbaImage figure, int height)
    {
        var scaled = Resampler.DownscaleToHeight(figure, height);
        var canvas = new RgbaImage(Pot(scaled.Width), Pot(scaled.Height));
        for (var y = 0; y < scaled.Height; y++)
        {
            Buffer.BlockCopy(scaled.Pixels, scaled.Offset(0, y), canvas.Pixels, canvas.Offset(0, y), scaled.Width * 4);
        }
        return (canvas, scaled);
    }

    /// <summary>The smallest power of two at least <paramref name="n"/>. WoW reloads those reliably.</summary>
    public static int Pot(int n)
    {
        var p = 1;
        while (p < n) p *= 2;
        return p;
    }
}
