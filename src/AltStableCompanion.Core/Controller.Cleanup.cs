namespace AltStableCompanion.Core;

public sealed partial class Controller
{
    /// <summary>
    /// "Clean up the character list" (#77), after the player's yes: the companion's files for
    /// each of these characters (<see cref="Leftovers.FilesOf"/>), then the manifest and the
    /// list again. Only characters still gone when it runs: one AltStable has again since the
    /// question keeps everything, and so does every character when an account file cannot be
    /// read - an unreadable file is not a deletion. Under the pass gate, so no pass or picture
    /// writes beside it. <paramref name="flavorDir"/> is the game folder the question was asked
    /// about: another one now deletes nothing, whatever its own leftovers (Codex on #78).
    /// Returns what it did, in a sentence for the window.
    /// </summary>
    public Task<string> CleanUpCharactersAsync(string flavorDir, IReadOnlyList<string> guids) => Task.Run(() => CleanUp(flavorDir, guids));

    private string CleanUp(string flavorDir, IReadOnlyList<string> guids)
    {
        WowInstall? install;
        int generation;
        lock (_gate)
        {
            if (_stopping) return "Not cleaned up: the app is closing.";
            install = _current.Shell.Install;
            generation = _generation;
        }
        if (install is null) return "Not cleaned up: no game folder is set.";
        if (!string.Equals(Path.GetFullPath(install.FlavorDir), Path.GetFullPath(flavorDir), StringComparison.OrdinalIgnoreCase))
        {
            return "Not cleaned up: the game folder changed since the question.";
        }
        var deleted = 0;
        var characters = 0;
        var kept = 0;
        var failed = new List<string>();
        lock (_passGate)
        {
            lock (_gate)
            {
                if (_stopping || generation != _generation) return "Not cleaned up: the game folder changed.";
            }
            var roster = Leftovers.Roster(install.AccountsDir, SavedVariablesReader.Snapshot(install.AccountsDir, m => _log?.Write(m)));
            if (roster is null)
            {
                _log?.Write("clean-up: an account file could not be read; nothing was deleted");
                return "Not cleaned up: an account file could not be read just now. Nothing was deleted; try again in a moment.";
            }
            var folder = new CutoutFolder(install.CutoutAddonDir);
            foreach (var guid in guids.Distinct(StringComparer.Ordinal))
            {
                if (roster.Contains(guid))
                {
                    kept++;
                    _log?.Write($"clean-up: {guid} is in AltStable again; kept");
                    continue;
                }
                var groups = Leftovers.FilesOf(guid, folder, install.Screenshots, OriginalsDir);
                if (groups.Count == 0) continue;
                characters++;
                foreach (var group in groups)
                {
                    // The owner goes last, and only after all it names: a file left behind keeps
                    // the sidecar or history that says whose it is, so it is found again.
                    var all = group.Files.Select(Delete).ToList().All(ok => ok);
                    if (group.Owner is not { } owner) continue;
                    if (all) Delete(owner);
                    else _log?.Write($"clean-up: kept {owner}, which names a file that could not be deleted");
                }
            }
            RefreshAfterEnhancement(folder, generation, null);
        }
        Changed?.Invoke();
        return PassText.CleanUpResult(characters, deleted, kept, failed);

        bool Delete(string file)
        {
            try
            {
                File.Delete(file);
                deleted++;
                _log?.Write($"clean-up: deleted {file}");
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failed.Add(Path.GetFileName(file));
                _log?.Write($"clean-up: could not delete {file}: {ex.Message}");
                return false;
            }
        }
    }

    /// <summary>Leave characters AltStable no longer has out of the list, without deleting anything.</summary>
    public void SetHideGone(bool hide)
    {
        lock (_gate)
        {
            if (_settings.HideGoneCharacters == hide) return;
            _current = _current with { HideGone = hide };
        }
        Save(s => s with { HideGoneCharacters = hide });
    }
}
