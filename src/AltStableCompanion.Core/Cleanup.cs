namespace AltStableCompanion.Core;

/// <summary>
/// Files deleted together: <see cref="Files"/> first, then <see cref="Owner"/> - the sidecar or
/// history that says whose they are - only once every one of them is gone. A file that could
/// not be deleted keeps its owner, so the rest can be retried (Codex on #78).
/// <see cref="Primary"/>: a portrait, capture or render - what makes the character a row of the
/// list, and so offered again. Those groups go last, and only once everything else of the
/// character is gone: a clean-up cut short leaves the character listed, to be tried again.
/// </summary>
public sealed record FileGroup(IReadOnlyList<string> Files, string? Owner, bool Primary = false)
{
    public IEnumerable<string> All => Owner is null ? Files : Files.Append(Owner);
}

/// <summary>
/// Characters AltStable no longer has (#77): a portrait in the companion's files for a GUID that
/// no account's <c>AltStableDB</c> holds - a character forgotten, deleted, or left over from the
/// beta. The list follows AltStable's: such a character is offered to "Clean up the character
/// list", which deletes the companion's files for it once the player says yes.
///
/// What it cannot delete: the capture records in <c>AltStable.lua</c>. The game rewrites that
/// file at logout, and the companion never writes it (AltStable#344 drops them on a forget).
/// </summary>
public static class Leftovers
{
    /// <summary>
    /// Every GUID in <c>AltStableDB</c> on any account, hidden ones included (AltStable still
    /// has them), or null when that is not known - and an unknown roster marks nobody gone and
    /// deletes nothing:
    /// <list type="bullet">
    /// <item>a file could not be read, or a roster stopped mid-way;</item>
    /// <item>a file has no <c>AltStableDB</c> table: absent is not empty;</item>
    /// <item>an account that uses AltStable has no <c>AltStable.lua</c> in the snapshot
    ///   (<see cref="AccountFiles"/>): the game replaces the file through a <c>.bak</c> and a
    ///   rename, and in between it is missing - or <c>File.Exists</c> said no for a file it
    ///   could not reach. Its characters are not gone for that.</item>
    /// </list>
    /// </summary>
    public static IReadOnlySet<string>? Roster(string accountsDir, SavedVariablesSnapshot snapshot)
    {
        if (RosterGuids(snapshot) is not { } guids) return null;
        try
        {
            var read = snapshot.Rosters.Select(r => Path.GetFullPath(r.Path)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            return AccountFiles(accountsDir).All(f => read.Contains(Path.GetFullPath(f))) ? guids : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The roster from these files alone: see <see cref="Roster"/>, which also checks no account's file is missing.</summary>
    public static IReadOnlySet<string>? RosterGuids(SavedVariablesSnapshot snapshot)
    {
        if (snapshot.Skipped.Count > 0 || snapshot.Rosters.Any(r => r.Problem is not null || !r.HasRoster)) return null;
        return snapshot.Rosters.SelectMany(r => r.Characters).Select(c => c.Guid).ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>
    /// The <c>AltStable.lua</c> each account that uses AltStable must have: an account whose
    /// SavedVariables holds any of the addon's files - <c>AltStable.lua</c> itself, its
    /// <c>.bak</c>, or its modules' (<c>AltStableRoster.lua</c>, ...).
    /// </summary>
    public static IEnumerable<string> AccountFiles(string accountsDir)
    {
        if (!Directory.Exists(accountsDir)) yield break;
        foreach (var account in Directory.EnumerateDirectories(accountsDir))
        {
            var saved = Path.Combine(account, "SavedVariables");
            if (Directory.Exists(saved) && Directory.EnumerateFiles(saved, "AltStable*").Any())
            {
                yield return Path.Combine(saved, SavedVariablesReader.FileName);
            }
        }
    }

    /// <summary>
    /// Every GUID the companion still keeps a file for: a capture sidecar, an armory sidecar,
    /// an enhanced sidecar that names it, an attempt history. One of these left by a clean-up
    /// cut short - the portrait went, its sidecar could not - keeps the character offered for
    /// another try, portrait or not (Codex on #78). Unreadable folders own nothing.
    /// </summary>
    public static IReadOnlySet<string> OwnedGuids(CutoutFolder folder)
    {
        var owned = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            foreach (var (dir, read) in new (string, Func<string, CutoutMeta?>)[]
                     { (folder.CutoutsDir, folder.ReadMeta), (folder.ArmoryDir, folder.ReadArmoryMeta), (folder.EnhancedDir, folder.ReadEnhancedMeta) })
            {
                if (!Directory.Exists(dir)) continue;
                foreach (var json in Directory.EnumerateFiles(dir, "*.json"))
                {
                    var fileBase = Path.GetFileNameWithoutExtension(json);
                    if (fileBase.EndsWith(".attempts", StringComparison.OrdinalIgnoreCase))
                    {
                        var guid = fileBase[..^".attempts".Length];
                        if (ManifestWriter.IsSafeKey(guid)) owned.Add(guid);
                        continue;
                    }
                    if (read(fileBase)?.Guid is { } g && ManifestWriter.IsSafeKey(g)) owned.Add(g);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // What could be listed is kept; a folder that could not be is nobody's this time.
        }
        return owned;
    }

    /// <summary>
    /// The files the companion keeps for one character, for "Clean up the character list", in
    /// the order to delete them - each group's owner last, and the portraits after the rest:
    /// <list type="bullet">
    /// <item>its enhanced picture (owned by its sidecar) - first, as an older one with no GUID
    ///   of its own is found by the base of the capture or render it was made from;</item>
    /// <item>the screenshots its capture cutouts were made from (their <c>shots</c>), still
    ///   there - with them, the next pass would make the cutout again;</item>
    /// <item>the originals kept for it that no other character's history names (owned by its
    ///   history);</item>
    /// <item>its portraits (<see cref="FileGroup.Primary"/>): its armory render, and its capture
    ///   cutouts - every one whose sidecar names it - each owned by its sidecar.</item>
    /// </list>
    /// Only files that exist, each once. Nothing for a GUID that is not one.
    /// </summary>
    public static IReadOnlyList<FileGroup> FilesOf(string guid, CutoutFolder folder, string screenshotsDir, string originalsDir)
    {
        if (!ManifestWriter.IsSafeKey(guid)) return [];
        var groups = new List<FileGroup>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var bases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        string? Take(string path) => File.Exists(path) && seen.Add(path) ? path : null;

        void Group(string? owner, bool primary, params IEnumerable<string> files)
        {
            var kept = files.Select(Take).OfType<string>().ToList();
            var own = owner is null ? null : Take(owner);
            if (kept.Count > 0 || own is not null) groups.Add(new FileGroup(kept, own, primary));
        }

        IEnumerable<string> Sidecars(string dir) =>
            Directory.Exists(dir) ? Directory.EnumerateFiles(dir, "*.json").Order(StringComparer.Ordinal) : [];

        // Whose bases they are, first: the enhanced pictures are found by them.
        var captures = Sidecars(folder.CutoutsDir)
            .Select(json => (Json: json, Base: Path.GetFileNameWithoutExtension(json)))
            .Select(x => (x.Json, x.Base, Meta: folder.ReadMeta(x.Base)))
            .Where(x => x.Meta?.Guid == guid).ToList();
        var renders = Sidecars(folder.ArmoryDir)
            .Select(json => (Json: json, Base: Path.GetFileNameWithoutExtension(json)))
            .Where(x => folder.ReadArmoryMeta(x.Base)?.Guid == guid).ToList();
        bases.UnionWith(captures.Select(c => c.Base));
        bases.UnionWith(renders.Select(r => r.Base));

        foreach (var json in Sidecars(folder.EnhancedDir))
        {
            var fileBase = Path.GetFileNameWithoutExtension(json);
            if (fileBase.EndsWith(".attempts", StringComparison.OrdinalIgnoreCase)) continue;
            var owner = folder.ReadEnhancedMeta(fileBase)?.Guid;
            if (owner != guid && !(owner is null && bases.Contains(fileBase))) continue;
            Group(json, false, Path.Combine(folder.EnhancedDir, fileBase + ".tga"));
        }
        // Owned by the cutouts' sidecars, which stay until the portraits go.
        // A name, never a path: the sidecar is a file anyone can edit.
        Group(null, false, captures.SelectMany(c => c.Meta!.Shots ?? []).Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => Path.Combine(screenshotsDir, Path.GetFileName(s))));

        var history = AttemptHistory.PathFor(folder.EnhancedDir, guid);
        if (File.Exists(history))
        {
            var originals = new List<string>();
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
                    if (!others.Contains(signature)) originals.Add(Path.Combine(originalsDir, signature + ".png"));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The originals stay: a few files in the app's folder, never drawn.
            }
            Group(history, false, originals);
        }

        foreach (var (json, fileBase) in renders) Group(json, true, Path.Combine(folder.ArmoryDir, fileBase + ".tga"));
        foreach (var (json, fileBase, _) in captures) Group(json, true, Path.Combine(folder.CutoutsDir, fileBase + ".tga"));
        return groups;
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
