namespace AltStableCompanion.Core;

/// <summary>What the window shows, as of now. Replaced whole, never changed in place.</summary>
public sealed record Snapshot(
    ShellState Shell,
    bool Pinned,
    bool KeepScreenshots,
    bool RestartNotice,
    string? SettingsProblem,
    /// <summary>One of <see cref="Skins"/>.</summary>
    string Skin = Skins.Clear,
    /// <summary>Enhanced portraits: on, the level from which, the style (one of <see cref="EnhanceStyles"/>).</summary>
    bool Enhance = false,
    int EnhanceMinLevel = Settings.DefaultEnhanceMinLevel,
    string EnhanceStyle = EnhanceStyles.WowLike,
    /// <summary>How the Codex CLI is signed in ("Logged in using ChatGPT"), "installed", or null: not on this PC.</summary>
    string? CodexStatus = null,
    /// <summary>Whether the CLI has been looked for yet: it is, once the setting is on.</summary>
    bool CodexProbed = false);

/// <summary>
/// Everything a shell does that is not drawing: which install, the watcher, the passes, the
/// settings. No UI type in it; it raises <see cref="Changed"/> on whatever thread it is on, and
/// the subscriber reads <see cref="Current"/> on its own.
///
/// The rules it keeps:
/// <list type="bullet">
/// <item>ONE pass at a time for the life of the app, whatever happens to the watcher. The
///   watcher serialises its own passes; replacing it (Browse) would otherwise let the old
///   one's pass and the new one's overlap.</item>
/// <item>A pass is never cancelled. It takes seconds, and one that is cut short has written
///   cutouts its manifest does not list. Browse and Quit wait for it.</item>
/// <item>A pass belongs to the install it was started for. Its report is dropped if the
///   install has changed since.</item>
/// <item>Nothing thrown by a pass leaves the pass: it runs on a timer thread, where an
///   exception ends the process.</item>
/// <item>Whether a pass may START is decided when it starts - after the wait for the pass
///   before it, not before. The player may have paused in between.</item>
/// <item>Settings that could not be read are not defaults. Which folder the player chose is
///   then UNKNOWN, and stays unknown - through other settings being changed, and through
///   restarts - until they choose one with Browse or Detect again. Nothing is detected for
///   them meanwhile, and screenshots are kept.</item>
/// <item>Settings that could not be WRITTEN are written again, at every pass, until they are.
///   What is owed to the player (the restart notice) must not depend on one write.</item>
/// <item>On the very first start NOTHING is converted until the player says so. A pass deletes
///   the screenshots it used and keeps the options it began with: an explanation that is
///   shown while the first pass runs comes too late for that pass.</item>
/// </list>
/// </summary>
public sealed partial class Controller(StartupOptions options, Func<WowInstall?>? detect = null,
    Func<DateTime>? clock = null, EnhanceHooks? enhance = null) : IDisposable
{
    private readonly Func<DateTime> _clock = clock ?? (() => DateTime.Now);
    // The way to Codex, or none: a controller made without one never enhances. The app hands
    // in the real one; tests hand in a shim, and there is no path from the shim to PATH.
    private readonly EnhanceHooks? _enhance = enhance;
    private const string UnreadableCopy = "settings.unreadable.json";

    private readonly Lock _passGate = new();                   // held for the length of a pass
    private readonly Lock _gate = new();                       // guards everything below
    private readonly string _dataDir = options.DataDir ?? Settings.DefaultDir;
    private Settings _settings = new();
    private bool _unreadOnDisk;                                // settings.json is still the file that could not be read
    private bool _saveOwed;                                    // the last write failed
    private string? _saveProblem;
    private Log? _log;
    private ConvertPass? _pass;
    private Watcher? _watcher;
    private int _generation;
    private int _lastGeneration;
    private int _asked;
    private bool _stopping;
    private Snapshot _current = new(new ShellState(), Pinned: false, KeepScreenshots: false,
        RestartNotice: false, SettingsProblem: null);

    /// <summary>Something in <see cref="Current"/> changed. Raised on any thread.</summary>
    public event Action? Changed;

    /// <summary>A pass of the CURRENT install finished. Raised on the pass's thread.</summary>
    public event Action<PassReport>? PassCompleted;

    public Snapshot Current
    {
        get { lock (_gate) return _current; }
    }

    /// <summary>A line for the log from the shell itself: the tray icon coming and going.</summary>
    public void Note(string message) => _log?.Write(message);

    public string LogPath => Path.Combine(_dataDir, "log.txt");
    public string DataDir => _dataDir;

    public void Start()
    {
        _log = new Log(_dataDir);
        _settings = Settings.Load(_dataDir, out var unread);
        if (unread is not null)
        {
            // What the player chose is in a file that cannot be read. Deleting is the one
            // thing that cannot be taken back, so until they say otherwise nothing is deleted.
            _settings = _settings with { KeepScreenshots = true, InstallUnknown = true };
            _unreadOnDisk = true;
            _log.Write(unread);
        }
        var resolved = ResolvedInstall.Resolve(options.WowDir, _settings.WowFlavorDir, detect,
            settingsProblem: _settings.InstallUnknown ? "unknown" : null);
        _log.Write($"started - install: {resolved.Install?.FlavorDir ?? "none"}"
            + (resolved.Problem is null ? "" : $" ({resolved.Problem})"));
        lock (_gate)
        {
            _current = _current with
            {
                Pinned = resolved.Pinned,
                KeepScreenshots = _settings.KeepScreenshots,
                Skin = _settings.Skin,
                Enhance = _settings.Enhance,
                EnhanceMinLevel = _settings.EnhanceMinLevel,
                EnhanceStyle = _settings.EnhanceStyle,
                SettingsProblem = Problem(),
                Shell = new ShellState(Paused: _settings.Paused, FirstStart: !_settings.Started),
            };
        }
        if (!Use(resolved.Install, resolved.Problem))
        {
            Use(null, "The WoW folder could not be set up - see the log");
        }
        StartEnhancer();
    }

    /// <summary>The player picked a folder. False when it is not a WoW folder: nothing changes.</summary>
    public bool Browse(string folder)
    {
        if (Current.Pinned) return false;
        if (ResolvedInstall.FromPicked(folder) is not { } install) return false;
        // Used first, saved after: a folder that could not be set up is not one to remember.
        if (!Use(install, null)) return false;
        Save(s => s with { WowFlavorDir = install.FlavorDir, InstallUnknown = false });
        return true;
    }

    /// <summary>Forget the chosen folder and look for the game again. The player's own choice.</summary>
    public void DetectAgain()
    {
        if (Current.Pinned) return;
        var resolved = ResolvedInstall.Resolve(null, null, detect);
        if (Use(resolved.Install, resolved.Problem))
        {
            Save(s => s with { WowFlavorDir = null, InstallUnknown = false });
        }
    }

    /// <summary>
    /// The first start is over: the player has read what happens to their screenshots and
    /// chosen. The settings are written now, so the next start is not a first one.
    /// </summary>
    public void StartWatching()
    {
        Watcher? watcher;
        lock (_gate)
        {
            if (!_current.Shell.FirstStart) return;
            _current = _current with { Shell = _current.Shell with { FirstStart = false } };
            watcher = _watcher;
        }
        Save(s => s with { Started = true });
        // The player pressed the button: the first look happens, paused or not.
        Interlocked.Exchange(ref _asked, 1);
        watcher?.RunNow();
    }

    /// <summary>"Got it": the player has read that portraits were written.</summary>
    public void AcknowledgeUpdate()
    {
        lock (_gate) _current = _current with { Shell = _current.Shell with { UpdateSeen = true } };
        Changed?.Invoke();
    }

    public void ConvertNow()
    {
        Watcher? watcher;
        lock (_gate) watcher = _stopping || _current.Shell.FirstStart ? null : _watcher;
        if (watcher is null) return;
        // The pass that runs next is one the player asked for, paused or not.
        Interlocked.Exchange(ref _asked, 1);
        watcher.RunNow();
    }

    public void SetSkin(string skin)
    {
        skin = Skins.Normalize(skin);
        lock (_gate)
        {
            if (_current.Skin == skin) return;
            _current = _current with { Skin = skin };
        }
        Save(s => s with { Skin = skin });
    }

    public void SetPaused(bool paused)
    {
        lock (_gate)
        {
            if (_watcher is not null) _watcher.Paused = paused;
            _current = _current with { Shell = _current.Shell with { Paused = paused } };
        }
        Save(s => s with { Paused = paused });
        if (!paused) WakeEnhancer();
    }

    /// <summary>
    /// The pass in hand finishes as it began; the next one uses the new setting. The watcher
    /// stays as it is - what it watches has not changed.
    /// </summary>
    public void SetKeepScreenshots(bool keep)
    {
        lock (_gate)
        {
            if (_current.KeepScreenshots == keep) return;
            _current = _current with { KeepScreenshots = keep };
            // The option, not the pass: the pass remembers the pairs it would not use.
            if (_pass is not null) _pass.Options = new ConvertOptions(KeepScreenshots: keep);
        }
        Save(s => s with { KeepScreenshots = keep });
    }

    public void DismissRestartNotice()
    {
        var install = Current.Shell.Install;
        if (install is null) return;
        lock (_gate) _current = _current with { RestartNotice = false };
        Save(s => s.WithRestartNotice(install.FlavorDir, owed: false));
    }

    /// <summary>
    /// Stop watching and wait for the pass in hand, without holding the caller: the UI thread
    /// goes on pumping while it waits.
    /// </summary>
    public Task StopAsync()
    {
        Watcher? watcher;
        lock (_gate)
        {
            _stopping = true;
            watcher = _watcher;
            _watcher = null;
            _current = _current with { Shell = _current.Shell with { Stopping = true } };
        }
        watcher?.Dispose();
        StopEnhancer();
        Changed?.Invoke();
        return Task.Run(async () =>
        {
            lock (_passGate) { }
            // The picture in flight was told to stop; its child is killed, its record written.
            if (_enhanceLoop is { } loop) await Task.WhenAny(loop, Task.Delay(TimeSpan.FromSeconds(15)));
            _log?.Write("stopped");
        });
    }

    /// <summary>
    /// Watch this install from now on. Everything that can fail is built BEFORE anything is
    /// replaced: when it returns false, the install in use is the one that was in use.
    /// </summary>
    private bool Use(WowInstall? install, string? problem)
    {
        // A number of its own, captured by the watcher's callback: a callback that arrives
        // after the install has changed again finds another number current and does nothing.
        var generation = Interlocked.Increment(ref _lastGeneration);
        ConvertPass? pass = null;
        Watcher? watcher = null;
        if (install is not null)
        {
            try
            {
                pass = new ConvertPass(install, new ConvertOptions(Current.KeepScreenshots), m => _log?.Write(m));
                watcher = Watcher.For(install, () => RunPass(generation), Failed);
            }
            catch (Exception ex)
            {
                watcher?.Dispose();
                _log?.Write($"could not watch {install.FlavorDir}: {ex}");
                return false;
            }
        }

        Watcher? old;
        lock (_gate)
        {
            if (_stopping)
            {
                watcher?.Dispose();
                return false;
            }
            _generation = generation;
            old = _watcher;
            _pass = pass;
            if (pass is not null) pass.Options = new ConvertOptions(KeepScreenshots: _current.KeepScreenshots);
            if (watcher is not null) watcher.Paused = _current.Shell.Paused;
            _watcher = watcher;
            // Detect again, or Browse to the folder already in use, finds the same game: what
            // was written there is still what was written there. Only another game starts empty.
            var same = install is not null && _current.Shell.Install is { } was
                && string.Equals(was.FlavorDir, install.FlavorDir, StringComparison.OrdinalIgnoreCase);
            _current = _current with
            {
                RestartNotice = RestartNoticeDue(install),
                Shell = _current.Shell with
                {
                    Install = install,
                    InstallProblem = problem,
                    Report = null,
                    LastError = null,
                    LastPass = same ? _current.Shell.LastPass : null,
                    LastWritten = same ? _current.Shell.LastWritten : null,
                    UpdateSeen = same && _current.Shell.UpdateSeen,
                },
            };
        }
        old?.Dispose();
        Changed?.Invoke();
        // The first look, at once - and not one the player asked for: it obeys a pause.
        watcher?.TriggerNow();
        return true;
    }

    private void RunPass(int generation)
    {
        lock (_passGate)
        {
            ConvertPass pass;
            WowInstall install;
            lock (_gate)
            {
                // The generation here is a second guard, and no test reaches it: a watcher that
                // has been replaced is disposed, and a disposed watcher starts nothing. What IS
                // tested is the check after the pass, which drops a report whose install has gone.
                if (_stopping || generation != _generation || _pass is null || _current.Shell.Install is null) return;
                // Asked here, with the gate in hand: this pass may have waited for another,
                // and the player may have paused while it did.
                var asked = Interlocked.Exchange(ref _asked, 0) == 1;
                if (_current.Shell.FirstStart) return;
                if (_current.Shell.Paused && !asked) return;
                pass = _pass;
                install = _current.Shell.Install;
                _current = _current with { Shell = _current.Shell with { Converting = true } };
            }
            Changed?.Invoke();

            PassReport? report = null;
            string? error = null;
            try
            {
                OweRestartNotice(install);
                report = pass.Run();
            }
            catch (Exception ex)
            {
                error = ex.Message;
                _log?.Write($"the pass failed: {ex}");
            }

            var current = false;
            lock (_gate)
            {
                // Whatever could not be written before, the restart notice included.
                if (_saveOwed) Write();
                current = generation == _generation;
                _current = _current with { Shell = _current.Shell with { Converting = false } };
                if (current)
                {
                    var note = PassNote.Of(report, _clock());
                    _current = _current with
                    {
                        RestartNotice = RestartNoticeDue(install),
                        Shell = _current.Shell with
                        {
                            Report = report ?? _current.Shell.Report,
                            LastError = error,
                            LastPass = note,
                            LastWritten = note.Written.Count > 0 ? note : _current.Shell.LastWritten,
                            // Something new was written: it has not been read yet.
                            UpdateSeen = note.Written.Count == 0 && _current.Shell.UpdateSeen,
                        },
                    };
                }
            }
            Changed?.Invoke();
            if (current && report is not null) PassCompleted?.Invoke(report);
            // A pass may have written a portrait somebody is now eligible for.
            if (current && report is not null) WakeEnhancer();
        }
    }

    // The watcher caught something RunPass did not: only a subscriber can have thrown it.
    private void Failed(Exception ex) => _log?.Write($"after the pass: {ex}");

    /// <summary>
    /// Written down BEFORE the pass that may create the addon, so the notice is owed even if
    /// the app is killed the moment after. An addon that is already there is never owed one.
    /// When the write fails the notice is owed in memory, and the write is tried again.
    /// </summary>
    private void OweRestartNotice(WowInstall install)
    {
        if (_settings.OwesRestartNotice(install.FlavorDir))
        {
            lock (_gate)
            {
                if (_saveOwed) Write();
            }
            return;
        }
        if (File.Exists(new CutoutFolder(install.CutoutAddonDir).TocPath)) return;
        Save(s => s.WithRestartNotice(install.FlavorDir, owed: true), quiet: true);
    }

    private bool RestartNoticeDue(WowInstall? install) =>
        install is not null
        && _settings.OwesRestartNotice(install.FlavorDir)
        && File.Exists(new CutoutFolder(install.CutoutAddonDir).TocPath);

    private void Save(Func<Settings, Settings> change, bool quiet = false)
    {
        lock (_gate)
        {
            _settings = change(_settings);
            Write();
        }
        if (!quiet) Changed?.Invoke();
    }

    // Under _gate. The file that could not be read is kept beside the new one: what the player
    // had chosen is in it, for them to look at.
    private void Write()
    {
        string? problem = null;
        try
        {
            if (_unreadOnDisk)
            {
                File.Copy(Path.Combine(_dataDir, "settings.json"), Path.Combine(_dataDir, UnreadableCopy), overwrite: true);
                _unreadOnDisk = false;
            }
            _settings.Save(_dataDir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            problem = $"Settings could not be saved: {ex.Message}";
        }
        _saveOwed = problem is not null;
        if (problem is not null && problem != _saveProblem) _log?.Write(problem);
        _saveProblem = problem;
        _current = _current with { SettingsProblem = Problem() };
    }

    private string? Problem() =>
        _saveProblem
        ?? (_settings.InstallUnknown
            ? "The settings could not be read, so the WoW folder has to be chosen again: Browse, or Detect again."
              + (_unreadOnDisk ? "" : $" The old file is kept as {UnreadableCopy}.")
            : null);

    public void Dispose()
    {
        Watcher? watcher;
        lock (_gate)
        {
            _stopping = true;
            watcher = _watcher;
            _watcher = null;
        }
        watcher?.Dispose();
        StopEnhancer();
    }
}
