namespace AltStableCompanion.Core;

public sealed partial class Controller
{
    /// <summary>
    /// Help, "Save diagnostics": the file, masked, written to <paramref name="folder"/>; its
    /// path. Reads the stores and the cutouts, so it belongs off the UI thread.
    /// </summary>
    public string SaveDiagnostics(string folder, string userProfile)
    {
        DiagnosticsFacts facts;
        lock (_gate)
        {
            var shell = _current.Shell;
            var how = _current.Pinned ? "given with --wow-dir"
                : _settings.InstallUnknown ? "not known: the settings could not be read"
                : _settings.WowFlavorDir is not null ? "chosen with Browse"
                : shell.Install is not null ? "detected"
                : "not found";
            if (shell.InstallProblem is { } problem) how += "; " + problem;
            facts = new DiagnosticsFacts(Version, shell.Install, how, _settings.Enhance, _current.CodexProbed,
                _current.CodexStatus, LogPath,
                _settings.Armory switch
                {
                    null => "not asked yet",
                    false => "off",
                    true => $"on ({(_settings.ArmorySd ? "SD" : "HD")}; an armory picture enhanced at most every {_settings.ArmoryRemakeDays} days)",
                }
                // Where the player's own API client stands, never the client itself.
                + _current.BlizzardClient switch
                {
                    BlizzardClientState.Saved => "; Blizzard API client saved",
                    BlizzardClientState.Refused => "; Blizzard API client refused this run",
                    BlizzardClientState.Unreadable => "; Blizzard API client cannot be read",
                    _ => "",
                });
        }
        var now = _clock();
        var path = Diagnostics.Save(Diagnostics.Build(facts, now, userProfile), folder, now);
        _log?.Write("diagnostics saved");
        return path;
    }
}
