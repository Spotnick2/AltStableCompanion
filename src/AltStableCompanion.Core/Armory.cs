using System.Globalization;

namespace AltStableCompanion.Core;

/// <summary>
/// Portraits from the Forever armory's renders (#63, docs/PORTRAIT-CONTRACT.md section 4). The
/// render is a public CDN file named after the GUID's player number: no account, no key.
///
/// Measured 2026-11-11 on Forever US: a 1600x1200 transparent 8-bit RGBA PNG, the figure about
/// 330x700, always in the same pose; <c>Last-Modified</c> and <c>ETag</c>, and conditional
/// requests answered 304; 403 for a character with no render. A render lands about seven hours
/// after the character was last played, with nobody looking at the armory page.
/// </summary>
public static class Armory
{
    public const string Host = "render.worldofwarcraft.com";

    /// <summary>How long after play a render lands, measured: what a render's date is moved back by to say when it was of.</summary>
    public static readonly TimeSpan RenderDelay = TimeSpan.FromHours(7);

    /// <summary>A clock difference this small is noise, not a PC whose clock is wrong.</summary>
    public const long SkewTolerance = 120;

    /// <summary>The armory has characters from this level.</summary>
    public const int MinLevel = 10;

    /// <summary>After play, looked at from this long after...</summary>
    public static readonly TimeSpan WindowStart = TimeSpan.FromHours(6);

    /// <summary>...to this long after, every <see cref="CheckEvery"/>.</summary>
    public static readonly TimeSpan WindowEnd = TimeSpan.FromHours(24);

    public static readonly TimeSpan CheckEvery = TimeSpan.FromHours(1);

    /// <summary>The settings' bounds and default for "remake an enhanced picture after an armory update at most every N days".</summary>
    public const int MinRemakeDays = 1;
    public const int MaxRemakeDays = 90;
    public const int DefaultRemakeDays = 7;

    /// <summary>The daily limit on automatic armory pictures: its default and its ceiling.</summary>
    public const int DefaultDailyMax = 5;
    public const int MaxDailyMax = 100;

    /// <summary>
    /// Two renders are the same picture when, at the same size, less than this share of the
    /// figure's pixels moved by more than <see cref="SameLevels"/>. Measured 2026-11-11 on
    /// Karuzo's two renders a gear change apart (shoulders and bracers): 9.9 %; the same render
    /// against itself: 0 %.
    /// </summary>
    public const double SameShare = 0.01;
    public const int SameLevels = 24;

    /// <summary>The only region whose render route is measured.</summary>
    public const string Region = "US";

    /// <summary>
    /// The player number of a GUID, <c>Player-&lt;realm&gt;-&lt;hex&gt;</c>, or null for anything
    /// else. The CDN names the render after it.
    /// </summary>
    public static long? PlayerNumber(string guid)
    {
        var parts = guid.Split('-');
        if (parts.Length != 3 || parts[0] != "Player" || parts[1].Length == 0 || !parts[1].All(char.IsAsciiDigit)) return null;
        var hex = parts[2];
        if (hex.Length is 0 or > 8 || !hex.All(char.IsAsciiHexDigit)) return null;
        var n = long.Parse(hex, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
        return n > 0 ? n : null;
    }

    /// <summary>The render's URL; <paramref name="sd"/> is the SD (classic) model's. Null for a GUID that is not a player's.</summary>
    public static string? Url(string guid, bool sd)
    {
        if (PlayerNumber(guid) is not { } n) return null;
        return string.Create(CultureInfo.InvariantCulture,
            $"https://{Host}/forever-us/character/{n % 256}/{n}-main{(sd ? "-sd" : "")}-raw.png");
    }

    /// <summary>
    /// Which of a capture and a render the manifest names: the render when it is newer by the
    /// estimate - it shows the character as of its date minus <see cref="RenderDelay"/> - and
    /// the capture when its epoch is at least that. A capture that does not say when it was
    /// taken (an older converter's) is older than any render. An estimate: a capture taken
    /// within minutes of a session's end can be ordered wrongly either way, and the next
    /// capture or render corrects it.
    /// </summary>
    public static bool RenderWins(long? captureEpoch, CutoutMeta render)
    {
        if (AsOf(render) is not { } asOf) return false;
        if (captureEpoch is not { } epoch) return true;
        return DateTimeOffset.FromUnixTimeSeconds(epoch) < asOf;
    }

    /// <summary>When the render shows the character, in this PC's clock, or null when its date cannot be read.</summary>
    public static DateTimeOffset? AsOf(CutoutMeta render)
    {
        if (!DateTimeOffset.TryParse(render.LastModified, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var made)) return null;
        var skew = render.Skew is { } s && Math.Abs(s) > SkewTolerance ? TimeSpan.FromSeconds(s) : TimeSpan.Zero;
        return made - skew - RenderDelay;
    }

    /// <summary>
    /// The render as a cutout: cropped to everything more than 2 % opaque (the matte's floor),
    /// never scaled up, top-left on a power-of-two canvas - a capture's shape.
    /// </summary>
    public static Cutout ToCutout(RgbaImage png, CutoutMeta meta)
    {
        int minX = png.Width, minY = png.Height, maxX = -1, maxY = -1;
        for (var y = 0; y < png.Height; y++)
        {
            var row = y * png.Width * 4;
            for (var x = 0; x < png.Width; x++)
            {
                if (png.Pixels[row + x * 4 + 3] <= Floor) continue;
                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
            }
        }
        if (maxX < 0) throw new ThumbnailException("the render is empty");
        var figure = png.Crop(minX, minY, maxX - minX + 1, maxY - minY + 1);
        var (canvas, scaled) = CutoutConverter.OnCanvas(figure, CutoutConverter.MaxHeight);
        return new Cutout(canvas, meta with
        {
            W = scaled.Width, H = scaled.Height, TexW = canvas.Width, TexH = canvas.Height,
            NativePx = [figure.Width, figure.Height], NativeUnit = null, NativeW = null, NativeH = null,
        });
    }

    /// <summary>
    /// Whether two cut renders are the same picture to the eye (<see cref="SameShare"/>): a
    /// render drawn again from the same character, which must not cost a new enhanced picture.
    /// </summary>
    public static bool NearlySame(RgbaImage a, RgbaImage b)
    {
        if (a.Width != b.Width || a.Height != b.Height) return false;
        long figure = 0, moved = 0;
        for (var i = 0; i < a.Pixels.Length; i += 4)
        {
            if (a.Pixels[i + 3] == 0 && b.Pixels[i + 3] == 0) continue;
            figure++;
            for (var c = 0; c < 4; c++)
            {
                if (Math.Abs(a.Pixels[i + c] - b.Pixels[i + c]) <= SameLevels) continue;
                moved++;
                break;
            }
        }
        return figure > 0 && moved < figure * SameShare;
    }

    /// <summary>
    /// How many automatic armory pictures were launched in the 24 hours before <paramref name="now"/>,
    /// in every character's history; the ones the player asked for do not count.
    /// </summary>
    public static int LaunchedToday(string enhancedDir, DateTime now)
    {
        if (!Directory.Exists(enhancedDir)) return 0;
        var count = 0;
        foreach (var file in Directory.EnumerateFiles(enhancedDir, "*.attempts.json"))
        {
            var guid = Path.GetFileName(file)[..^".attempts.json".Length];
            try
            {
                count += AttemptHistory.Load(enhancedDir, guid).Attempts
                    .Count(a => a.Source == Attempt.FromArmory && a.Confirmed != true && a.Started > now.AddDays(-1) && a.Started <= now);
            }
            catch (Exception ex) when (ex is AttemptHistoryException or ArgumentException)
            {
                // Not readable: that character is refused its own launch; it does not count here.
            }
        }
        return count;
    }

    // alpha <= 0.02 is fully transparent, as in the matte.
    private const int Floor = 5;

    /// <summary>
    /// The game's region, from <c>WTF\Config.wtf</c>'s <c>SET portal "US"</c>, upper-case; null
    /// when it says nothing or cannot be read.
    /// </summary>
    public static string? RegionOf(WowInstall install)
    {
        var path = Path.Combine(install.FlavorDir, "WTF", "Config.wtf");
        try
        {
            if (!File.Exists(path)) return null;
            foreach (var line in File.ReadLines(path))
            {
                var t = line.Trim();
                if (!t.StartsWith("SET portal ", StringComparison.OrdinalIgnoreCase)) continue;
                var value = t["SET portal ".Length..].Trim().Trim('"').Trim();
                return value.Length == 0 ? null : value.ToUpperInvariant();
            }
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// When the character's automatic armory enhancements may run again: the last armory
    /// attempt (launched, whatever came of it, confirmed or not) plus the setting's days. Null
    /// when nothing holds it.
    /// </summary>
    public static DateTime? HeldUntil(AttemptHistory history, int days, DateTime now)
    {
        var last = history.Attempts.Where(a => a.Source == Attempt.FromArmory).Select(a => (DateTime?)a.Started).Max();
        if (last is not { } anchor) return null;
        var until = anchor.AddDays(Math.Clamp(days, MinRemakeDays, MaxRemakeDays));
        return now < until ? until : null;
    }
}

/// <summary>A character the armory may be asked about: in the roster, of the armory's levels, hidden nowhere.</summary>
/// <param name="Activity">When it was last played, as far as the roster says: the newest of <c>lastUpdate</c> and <c>gearAt</c> on any account, UTC.</param>
public sealed record ArmoryCharacter(string Guid, string Name, int Level, DateTimeOffset? Activity);

public static class ArmoryEligibility
{
    /// <summary>
    /// Every character the armory may be asked about, or nobody and why. A file that could not
    /// be read is not a view of who is hidden: nobody, as for enhancement - a request names a
    /// character to Blizzard, and cannot be taken back.
    /// </summary>
    /// <param name="minLevel">The armory draws from level 10; the API is asked about every level (M3).</param>
    public static (IReadOnlyList<ArmoryCharacter> Characters, string? Refused) Select(SavedVariablesSnapshot snapshot, int minLevel = Armory.MinLevel)
    {
        if (snapshot.Skipped.Count > 0) return ([], $"{snapshot.Skipped.Count} account file(s) could not be read this time");
        if (snapshot.Rosters.FirstOrDefault(r => r.Problem is not null) is { } broken) return ([], $"a roster could not be read ({broken.Problem})");
        var hidden = snapshot.Rosters.SelectMany(r => r.Hidden).ToHashSet(StringComparer.Ordinal);
        // As the enhancement settles it: the name from the most recently updated record (a tie
        // keeps the file that sorts first), the highest level; and the latest play on any.
        var newest = new Dictionary<string, RosterCharacter>(StringComparer.Ordinal);
        var level = new Dictionary<string, int>(StringComparer.Ordinal);
        var played = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var roster in snapshot.Rosters.OrderBy(r => r.Path, StringComparer.OrdinalIgnoreCase))
        {
            foreach (var c in roster.Characters)
            {
                if (hidden.Contains(c.Guid) || Armory.PlayerNumber(c.Guid) is null) continue;
                if (!newest.TryGetValue(c.Guid, out var have) || c.Updated > have.Updated) newest[c.Guid] = c;
                level[c.Guid] = Math.Max(c.Level, level.GetValueOrDefault(c.Guid));
                played[c.Guid] = Math.Max(Math.Max(c.Updated, c.GearAt), played.GetValueOrDefault(c.Guid));
            }
        }
        return ([.. newest.Values
            .Select(c => new ArmoryCharacter(c.Guid, c.Name, level[c.Guid],
                played[c.Guid] > 0 ? DateTimeOffset.FromUnixTimeSeconds(played[c.Guid]) : null))
            .Where(c => c.Level >= minLevel)
            .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ThenBy(c => c.Guid, StringComparer.Ordinal)], null);
    }
}

/// <summary>
/// When a character's render is looked at. Nothing about it is written down: the window after
/// play comes from the roster's own times, so a restart finds it again.
/// <list type="bullet">
/// <item>Once a run (the start, "Check the armory now"): a render that changed while the app
///   was closed - another PC, the weekly reset - is found.</item>
/// <item>From <see cref="Armory.WindowStart"/> to <see cref="Armory.WindowEnd"/> after it was last
///   played, every <see cref="Armory.CheckEvery"/>: when the new render is due.</item>
/// <item>Once when a hold ends: the update it held back.</item>
/// </list>
/// </summary>
public static class ArmoryPlanner
{
    public static bool Due(ArmoryCharacter c, DateTimeOffset? lastChecked, bool checkedThisRun, bool heldBefore, DateTimeOffset now)
    {
        if (!checkedThisRun || heldBefore) return true;
        if (c.Activity is not { } played) return false;
        if (now < played + Armory.WindowStart || now > played + Armory.WindowEnd) return false;
        return lastChecked is not { } last || now - last >= Armory.CheckEvery;
    }
}
