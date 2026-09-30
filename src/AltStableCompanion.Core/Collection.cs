using System.Globalization;

namespace AltStableCompanion.Core;

/// <summary>How a row's portrait was found - the way the Roster finds it, in that order.</summary>
public enum PortraitSource
{
    /// <summary>No portrait in the manifest for this character.</summary>
    None,
    /// <summary>The manifest entry keyed by the character's GUID.</summary>
    ByGuid,
    /// <summary>
    /// The entry keyed by the slug of its NAME, which names no GUID. The game shows it on this
    /// character; whose portrait it is, nobody wrote down.
    /// </summary>
    ByName,
    /// <summary>A portrait in the manifest that no character with a capture record resolved to.</summary>
    File,
}

/// <summary>What became of a character's latest capture. A separate fact from its portrait.</summary>
public enum CaptureOutcome
{
    /// <summary>No capture record: the row is a portrait file.</summary>
    None,
    /// <summary>The portrait on disk was made from it.</summary>
    Converted,
    /// <summary>The portrait is from a converter that did not record which capture it used.</summary>
    Unknown,
    /// <summary>Its screenshots are not on disk (consumed, deleted, another machine).</summary>
    NoScreenshots,
    /// <summary>A screenshot is in use or still being written; the next pass looks again.</summary>
    Writing,
    /// <summary>Looked at and not used: collided, rejected, ambiguous.</summary>
    Unusable,
    /// <summary>Converting it failed: a file that could not be written.</summary>
    Failed,
}

/// <summary>
/// One line of the window's list. The PORTRAIT and the LATEST CAPTURE are two facts with two
/// times, and neither stands in for the other: an older portrait can be ready while the newest
/// capture was rejected. <paramref name="Size"/> is the manifest's crop of the file, what the
/// game draws and what a thumbnail shows; null without a file.
/// </summary>
public sealed record PortraitRow(
    string Name,
    string? Guid,
    string? FileName,
    PortraitSource Source,
    DateTime? FileModified,
    DateTime? LatestCapture,
    CaptureOutcome Outcome,
    string? Note,
    bool NearlySquare = false,
    bool ShowGuid = false,
    (int W, int H)? Size = null,
    /// <summary>The enhanced picture attached to this portrait, if one is: what the game draws, and the thumbnail shows.</summary>
    EnhancedTexture? Enhanced = null,
    /// <summary>What the enhancer last did for this character, in a few words, or null.</summary>
    string? EnhanceNote = null,
    /// <summary>When the enhanced picture was written, if one is attached.</summary>
    DateTime? EnhancedModified = null)
{
    /// <summary>A picture of this character is being made right now.</summary>
    public bool Enhancing => EnhanceNote == CutoutFolder.EnhancingNote;

    /// <summary>The latest thing that happened to this character - a capture, a portrait, an enhanced picture - for the list's order.</summary>
    public DateTime? LastActivity => new[] { FileModified, EnhancedModified, LatestCapture }.Max();

    /// <summary>The file the thumbnail is made from: the enhanced picture when there is one.</summary>
    public string? ThumbnailFile => Enhanced is null ? FileName : FileName is null ? null : Path.Combine("Enhanced", FileName);
    public (int W, int H)? ThumbnailSize => Enhanced is { } e ? (e.W, e.H) : Size;

    /// <summary>In the manifest on disk. What the game makes of it, the app cannot see.</summary>
    public bool Ready => Source != PortraitSource.None;

    /// <summary>
    /// Something for the player to do or to know: a capture that could not be used, one that
    /// failed, a portrait that looks wrong, or a character with no portrait and nothing to
    /// make one from. An older portrait whose newer capture has no screenshots is NOT one: the
    /// portrait is there, and that capture is gone for good.
    /// </summary>
    public bool NeedsAttention =>
        Outcome is CaptureOutcome.Unusable or CaptureOutcome.Failed
        || (Ready && NearlySquare)
        || (!Ready && Outcome == CaptureOutcome.NoScreenshots);
}

/// <summary>
/// The list the window shows: every portrait in the manifest, and every character with a
/// capture record, each once.
///
/// A CHARACTER is a GUID with a capture record. Its portrait is looked up the way the Roster
/// does (CutoutFor in AltStableRoster.lua): the entry keyed by its GUID; else the entry keyed by
/// the slug of its name, unless that entry names another GUID.
///
/// Whatever is left in the manifest is a PORTRAIT FILE: labelled from its file name, which is a
/// label and not a claim about whose portrait it is. Two characters whose names share a slug
/// both resolve to one such file, and both rows say so.
/// </summary>
public static class Collection
{
    public static IReadOnlyList<PortraitRow> Build(
        IReadOnlyList<ManifestEntry> entries,
        IReadOnlyList<CharacterStatus> captures,
        IReadOnlyDictionary<string, DateTime> fileTimes,
        Func<string, string?>? enhanceNote = null)
    {
        var byKey = new Dictionary<string, ManifestEntry>(StringComparer.Ordinal);
        foreach (var e in entries) byKey.TryAdd(e.Key, e);

        var rows = new List<PortraitRow>();
        var claimed = new HashSet<ManifestEntry>();
        foreach (var c in captures)
        {
            var (entry, source) = Find(byKey, c);
            if (entry is not null) claimed.Add(entry);
            rows.Add(new PortraitRow(
                c.Name, c.Guid, entry?.FileName, source,
                entry is null ? null : Modified(fileTimes, entry),
                c.LastCaptured, OutcomeOf(c), c.Note,
                NearlySquare: c.NearlySquare,
                Size: entry is null ? null : (entry.W, entry.H),
                Enhanced: entry?.Enhanced,
                EnhanceNote: enhanceNote?.Invoke(c.Guid),
                EnhancedModified: entry is null ? null : EnhancedModified(fileTimes, entry)));
        }

        foreach (var e in entries.Where(e => !claimed.Contains(e)))
        {
            rows.Add(new PortraitRow(
                Label(Path.GetFileNameWithoutExtension(e.FileName)), e.Guid, e.FileName, PortraitSource.File,
                Modified(fileTimes, e), null, CaptureOutcome.None, null, Size: (e.W, e.H),
                Enhanced: e.Enhanced, EnhanceNote: e.Guid is null ? null : enhanceNote?.Invoke(e.Guid),
                EnhancedModified: EnhancedModified(fileTimes, e)));
        }

        // Namesakes are told apart by the one thing that is theirs alone.
        var shared = rows.Where(r => r.Guid is not null)
            .GroupBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // The one being worked on first, then the latest activity first, then the name: what
        // just happened is at the top, where a glance lands.
        return [.. rows
            .Select(r => r.Guid is not null && shared.Contains(r.Name) ? r with { ShowGuid = true } : r)
            .OrderByDescending(r => r.Enhancing)
            .ThenByDescending(r => r.LastActivity ?? DateTime.MinValue)
            .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Guid, StringComparer.Ordinal)
            .ThenBy(r => r.FileName, StringComparer.OrdinalIgnoreCase)];
    }

    private static (ManifestEntry? Entry, PortraitSource Source) Find(
        Dictionary<string, ManifestEntry> byKey, CharacterStatus c)
    {
        if (byKey.TryGetValue(c.Guid, out var mine)) return (mine, PortraitSource.ByGuid);
        var slug = Slugger.Slug(c.Name);
        if (slug.Length > 0 && byKey.TryGetValue(slug, out var named)
            && (named.Guid is null || named.Guid == c.Guid))
        {
            return (named, PortraitSource.ByName);
        }
        return (null, PortraitSource.None);
    }

    private static CaptureOutcome OutcomeOf(CharacterStatus c) => c.State switch
    {
        CharacterState.Portrait => c.Undated ? CaptureOutcome.Unknown : CaptureOutcome.Converted,
        CharacterState.Missing => c.Transient ? CaptureOutcome.Writing : CaptureOutcome.NoScreenshots,
        CharacterState.Failed => CaptureOutcome.Failed,
        _ => CaptureOutcome.Unusable,
    };

    private static DateTime? Modified(IReadOnlyDictionary<string, DateTime> times, ManifestEntry e) =>
        times.TryGetValue(e.FileName, out var t) ? t : null;

    private static DateTime? EnhancedModified(IReadOnlyDictionary<string, DateTime> times, ManifestEntry e) =>
        e.Enhanced is not null && times.TryGetValue(CutoutFolder.EnhancedKey(e.FileName), out var t) ? t : null;

    /// <summary>"karuzo-elegia" reads "Karuzo Elegia". A way to read a file name, nothing more.</summary>
    public static string Label(string fileBase)
    {
        var words = fileBase.Split('-', StringSplitOptions.RemoveEmptyEntries);
        return words.Length == 0
            ? fileBase
            : string.Join(' ', words.Select(w => char.ToUpper(w[0], CultureInfo.InvariantCulture) + w[1..]));
    }
}
