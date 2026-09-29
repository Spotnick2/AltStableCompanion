using System.Globalization;
using System.Text.RegularExpressions;

namespace AltStableCompanion.Core;

/// <summary>One capture: a shot-1 record paired with the next shot-2 record for the same GUID.</summary>
public sealed record Capture(
    string Name,
    string Guid,
    DateTime First,
    DateTime Second,
    long? Epoch,
    int? ScreenH,
    string StorePath)
{
    /// <summary>Both shots in one second: one file, the second overwrote the first. Unrecoverable.</summary>
    public bool Collided => First == Second;

    /// <summary>
    /// What "newest" is measured by: the epoch, when the record has one. Local time repeats an
    /// hour when the clocks go back, so a capture after the change can carry an EARLIER stamp.
    /// </summary>
    public double OrderKey => Epoch ?? new DateTimeOffset(First).ToUnixTimeSeconds();
}

/// <summary>A capture matched to its two screenshots - or the reason it could not be.</summary>
public sealed record Assignment(Capture Capture, string? Black, string? White, MatchProblem Problem);

public enum MatchProblem
{
    None,
    /// <summary>One or both screenshots are not on disk (converted, deleted, another machine).</summary>
    Missing,
    /// <summary>More than one file fits, or the files do not look like this pair. Nothing is touched.</summary>
    Ambiguous,
    /// <summary>Both shots landed in the same second.</summary>
    Collided,
}

public static partial class CapturePairing
{
    public static readonly TimeSpan Tolerance = TimeSpan.FromSeconds(4);

    /// <summary>
    /// Every capture, oldest first. Pairing is PER STORE: both shots of a capture are recorded
    /// by the same client, and two accounts shooting at the same moment must not have their
    /// halves paired with each other. A shot 1 with no following shot 2 is an abandoned capture.
    /// </summary>
    public static IReadOnlyList<Capture> Pair(IEnumerable<PortraitStore> stores)
    {
        var all = new List<Capture>();
        foreach (var store in stores.Where(s => s.Refused is null))
        {
            var pending = new Dictionary<string, RenderRecord>();
            foreach (var r in store.Renders)
            {
                if (r.Shot == 1)
                {
                    pending[r.Guid] = r;
                }
                else if (r.Shot == 2 && pending.Remove(r.Guid, out var first))
                {
                    all.Add(new Capture(first.Name, r.Guid, first.Stamp, r.Stamp, first.Epoch,
                        first.ScreenH, store.Path));
                }
            }
        }
        return [.. all.OrderBy(c => c.OrderKey)];
    }

    /// <summary>The newest capture of each character, by GUID - two characters can share a name.</summary>
    public static IReadOnlyList<Capture> NewestPerGuid(IEnumerable<Capture> captures) =>
        [.. captures.GroupBy(c => c.Guid).Select(g => g.MaxBy(c => c.OrderKey)!).OrderBy(c => c.OrderKey)];

    /// <summary>Screenshot path -> the second it was taken, from its own name: WoWScrnShot_MMDDYY_HHMMSS.tga.</summary>
    public static IReadOnlyDictionary<string, DateTime> ShotTimes(string folder)
    {
        var found = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(folder)) return found;
        foreach (var path in Directory.EnumerateFiles(folder, "WoWScrnShot_*.tga"))
        {
            var m = ShotName().Match(Path.GetFileName(path));
            if (m.Success && DateTime.TryParseExact(m.Groups[1].Value + m.Groups[2].Value, "MMddyyHHmmss",
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out var when))
            {
                found[path] = when;
            }
        }
        return found;
    }

    /// <summary>
    /// Match captures to screenshots for one pass.
    ///
    /// Stricter than make-cutout.py, deliberately (Codex review, #89). There, each stamp took
    /// the nearest file within tolerance and only the pair's own first file was excluded - so a
    /// shutter that silently failed could borrow the next file along, a hand-taken screenshot
    /// included, and a second account capturing in the same seconds could claim the same files.
    /// Here:
    /// <list type="bullet">
    /// <item>each stamp takes its nearest file within tolerance, and a TIE is ambiguous;</item>
    /// <item>the two files must be spaced as the two stamps were, to the second - a black shot
    ///   that is really somebody else's file fails this;</item>
    /// <item>a file wanted by two captures in the pass is ambiguous for BOTH.</item>
    /// </list>
    /// An ambiguous capture is left pending and nothing of it is deleted.
    /// </summary>
    public static IReadOnlyList<Assignment> Assign(IEnumerable<Capture> captures, IReadOnlyDictionary<string, DateTime> times)
    {
        var proposed = new List<Assignment>();
        foreach (var cap in captures)
        {
            if (cap.Collided)
            {
                proposed.Add(new Assignment(cap, null, null, MatchProblem.Collided));
                continue;
            }
            var (black, blackTie) = Nearest(cap.First, times, exclude: null);
            var (white, whiteTie) = Nearest(cap.Second, times, exclude: black);
            if (black is null || white is null)
            {
                proposed.Add(new Assignment(cap, black, white, MatchProblem.Missing));
                continue;
            }
            var expected = cap.Second - cap.First;
            var actual = times[white] - times[black];
            var spacedRight = Math.Abs((actual - expected).TotalSeconds) < 1;
            proposed.Add(blackTie || whiteTie || !spacedRight
                ? new Assignment(cap, black, white, MatchProblem.Ambiguous)
                : new Assignment(cap, black, white, MatchProblem.None));
        }

        // A file two captures both want belongs to neither, this pass.
        var wanted = proposed.Where(a => a.Problem == MatchProblem.None)
            .SelectMany(a => new[] { a.Black!, a.White! })
            .GroupBy(p => p, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return [.. proposed.Select(a => a.Problem == MatchProblem.None && (wanted.Contains(a.Black!) || wanted.Contains(a.White!))
            ? a with { Problem = MatchProblem.Ambiguous }
            : a)];
    }

    /// <summary>
    /// Are the client's records behind its screenshots? Right after a capture both images are on
    /// disk and the record of whose they are is not - it is written on reload or logout. Returns
    /// the newest screenshot and newest record times when that gap is real, else null.
    /// </summary>
    public static (DateTime NewestShot, DateTime? NewestRecord)? StoreIsStale(
        IEnumerable<PortraitStore> stores, IReadOnlyDictionary<string, DateTime> times, TimeSpan? slack = null)
    {
        if (times.Count == 0) return null;
        var newestShot = times.Values.Max();
        DateTime? newestRecord = null;
        foreach (var r in stores.SelectMany(s => s.Renders))
        {
            if (newestRecord is null || r.Stamp > newestRecord) newestRecord = r.Stamp;
        }
        if (newestRecord is null) return (newestShot, null);
        return newestShot - newestRecord.Value > (slack ?? TimeSpan.FromSeconds(60))
            ? (newestShot, newestRecord)
            : null;
    }

    private static (string? Path, bool Tie) Nearest(DateTime want, IReadOnlyDictionary<string, DateTime> times, string? exclude)
    {
        string? best = null;
        double bestGap = double.MaxValue;
        var tie = false;
        foreach (var (path, when) in times)
        {
            if (exclude is not null && string.Equals(path, exclude, StringComparison.OrdinalIgnoreCase)) continue;
            var gap = Math.Abs((when - want).TotalSeconds);
            if (gap > Tolerance.TotalSeconds) continue;
            if (gap < bestGap)
            {
                best = path;
                bestGap = gap;
                tie = false;
            }
            else if (gap == bestGap)
            {
                tie = true;
            }
        }
        return (best, tie);
    }

    [GeneratedRegex(@"^WoWScrnShot_(\d{6})_(\d{6})\.tga$", RegexOptions.IgnoreCase)]
    private static partial Regex ShotName();
}
