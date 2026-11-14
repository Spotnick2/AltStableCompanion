namespace AltStableCompanion.Core;

/// <summary>
/// Characters AltStable no longer has (#77): a portrait in the companion's files for a GUID that
/// no account's <c>AltStableDB</c> holds - a character forgotten, deleted, or left over from the
/// beta. The list follows AltStable's: such a character is offered to "Clean up the character
/// list", which deletes the companion's files for it once the player says yes.
///
/// What it cannot delete: the capture records in <c>AltStable.lua</c>. The game rewrites that
/// file at logout, and the companion never writes it (AltStable#343 is the addon's side).
/// </summary>
public static class Leftovers
{
    /// <summary>
    /// Every GUID in <c>AltStableDB</c> on any account, hidden ones included (AltStable still
    /// has them). Null when a file could not be read, or a roster stopped mid-way: what it
    /// would have said is not known, and an unreadable file is not a deletion.
    /// </summary>
    public static IReadOnlySet<string>? RosterGuids(SavedVariablesSnapshot snapshot)
    {
        if (snapshot.Skipped.Count > 0 || snapshot.Rosters.Any(r => r.Problem is not null)) return null;
        return snapshot.Rosters.SelectMany(r => r.Characters).Select(c => c.Guid).ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>
    /// The files the companion keeps for one character, for "Clean up the character list":
    /// <list type="bullet">
    /// <item>its capture cutouts, <c>Cutouts\&lt;base&gt;.tga</c> and <c>.json</c> - every one whose
    ///   sidecar names it, the older ones a rename left behind too;</item>
    /// <item>the screenshots those cutouts were made from (their sidecar's <c>shots</c>), where
    ///   they are still there: with them, the next pass would make the cutout again;</item>
    /// <item>its armory render and its enhanced picture, with their sidecars;</item>
    /// <item>its enhancement history, and the originals kept for it that no other character's
    ///   history names.</item>
    /// </list>
    /// Only files that exist, each once. Nothing for a GUID that is not one.
    /// </summary>
    public static IReadOnlyList<string> FilesOf(string guid, CutoutFolder folder, string screenshotsDir, string originalsDir)
    {
        if (!ManifestWriter.IsSafeKey(guid)) return [];
        var files = new List<string>();
        var bases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string path)
        {
            if (File.Exists(path) && !files.Contains(path, StringComparer.OrdinalIgnoreCase)) files.Add(path);
        }

        IEnumerable<string> Sidecars(string dir) =>
            Directory.Exists(dir) ? Directory.EnumerateFiles(dir, "*.json").Order(StringComparer.Ordinal) : [];

        foreach (var json in Sidecars(folder.CutoutsDir))
        {
            var fileBase = Path.GetFileNameWithoutExtension(json);
            if (folder.ReadMeta(fileBase) is not { } meta || meta.Guid != guid) continue;
            bases.Add(fileBase);
            Add(Path.Combine(folder.CutoutsDir, fileBase + ".tga"));
            Add(json);
            // A name, never a path: the sidecar is a file anyone can edit.
            foreach (var shot in meta.Shots ?? [])
            {
                if (!string.IsNullOrWhiteSpace(shot)) Add(Path.Combine(screenshotsDir, Path.GetFileName(shot)));
            }
        }
        foreach (var json in Sidecars(folder.ArmoryDir))
        {
            var fileBase = Path.GetFileNameWithoutExtension(json);
            if (folder.ReadArmoryMeta(fileBase)?.Guid != guid) continue;
            bases.Add(fileBase);
            Add(Path.Combine(folder.ArmoryDir, fileBase + ".tga"));
            Add(json);
        }
        // Enhanced\ is keyed by the base of the capture or render: its sidecar names the GUID,
        // and an older one that names none goes with the base it sits under.
        foreach (var json in Sidecars(folder.EnhancedDir))
        {
            var fileBase = Path.GetFileNameWithoutExtension(json);
            if (fileBase.EndsWith(".attempts", StringComparison.OrdinalIgnoreCase)) continue;
            var owner = folder.ReadEnhancedMeta(fileBase)?.Guid;
            if (owner != guid && !(owner is null && bases.Contains(fileBase))) continue;
            Add(Path.Combine(folder.EnhancedDir, fileBase + ".tga"));
            Add(json);
        }

        var history = AttemptHistory.PathFor(folder.EnhancedDir, guid);
        if (File.Exists(history))
        {
            Add(history);
            try
            {
                // Another character's history that cannot be read may name them: then they stay.
                var others = new HashSet<string>(StringComparer.Ordinal);
                var unknown = false;
                foreach (var p in Directory.EnumerateFiles(folder.EnhancedDir, "*.attempts.json"))
                {
                    if (string.Equals(p, history, StringComparison.OrdinalIgnoreCase)) continue;
                    if (SignaturesIn(folder.EnhancedDir, Path.GetFileName(p)[..^".attempts.json".Length]) is { } named) others.UnionWith(named);
                    else unknown = true;
                }
                foreach (var signature in unknown ? [] : SignaturesIn(folder.EnhancedDir, guid) ?? [])
                {
                    if (!others.Contains(signature)) Add(Path.Combine(originalsDir, signature + ".png"));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The originals stay: a few files in the app's folder, never drawn.
            }
        }
        return files;
    }

    // The signatures a history names, or null when it cannot be read.
    private static List<string>? SignaturesIn(string enhancedDir, string guid)
    {
        try
        {
            return [.. AttemptHistory.Load(enhancedDir, guid).Attempts.Select(a => a.Signature)
                .Where(s => s.Length > 0 && ManifestWriter.IsSafeKey(s))];
        }
        catch (Exception ex) when (ex is AttemptHistoryException or ArgumentException)
        {
            return null;
        }
    }
}
