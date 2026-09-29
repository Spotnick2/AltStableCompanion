using AltStableCompanion.Core;

namespace AltStableCompanion.App;

/// <summary>What the window shows, as of now. Replaced whole, never changed in place.</summary>
internal sealed record Snapshot(
    ShellState Shell,
    bool Pinned,
    bool KeepScreenshots,
    bool RestartNotice,
    string? SettingsProblem);

/// <summary>
/// Everything the shell does that is not drawing: which install, the watcher, the passes, the
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
/// </list>
/// </summary>
internal sealed class Controller(StartupOptions options) : IDisposable
{
    private readonly Lock _passGate = new();                   // held for the length of a pass
    private readonly Lock _gate = new();                       // guards everything below
    private readonly string _dataDir = options.DataDir ?? Settings.DefaultDir;
    private Settings _settings = new();
    private Log? _log;
    private ConvertPass? _pass;
    private Watcher? _watcher;
    private int _generation;
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
        _settings = Settings.Load(_dataDir);
        var resolved = ResolvedInstall.Resolve(options.WowDir, _settings.WowFlavorDir);
        _log.Write($"started - install: {resolved.Install?.FlavorDir ?? "none"}"
            + (resolved.Problem is null ? "" : $" ({resolved.Problem})"));
        lock (_gate)
        {
            _current = _current with
            {
                Pinned = resolved.Pinned,
                KeepScreenshots = _settings.KeepScreenshots,
                Shell = new ShellState(Paused: _settings.Paused),
            };
        }
        Use(resolved.Install, resolved.Problem);
    }

    /// <summary>The player picked a folder. False when it is not a WoW folder: nothing changes.</summary>
    public bool Browse(string folder)
    {
        if (Current.Pinned) return false;
        if (ResolvedInstall.FromPicked(folder) is not { } install) return false;
        Save(s => s with { WowFlavorDir = install.FlavorDir });
        Use(install, null);
        return true;
    }

    /// <summary>Forget the chosen folder and look for the game again.</summary>
    public void DetectAgain()
    {
        if (Current.Pinned) return;
        Save(s => s with { WowFlavorDir = null });
        var resolved = ResolvedInstall.Resolve(null, null);
        Use(resolved.Install, resolved.Problem);
    }

    public void ConvertNow()
    {
        Watcher? watcher;
        lock (_gate) watcher = _stopping ? null : _watcher;
        watcher?.RunNow();
    }

    public void SetPaused(bool paused)
    {
        lock (_gate)
        {
            if (_watcher is not null) _watcher.Paused = paused;
            _current = _current with { Shell = _current.Shell with { Paused = paused } };
        }
        Save(s => s with { Paused = paused });
        Changed?.Invoke();
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
            if (_current.Shell.Install is { } install) _pass = NewPass(install, keep);
        }
        Save(s => s with { KeepScreenshots = keep });
        Changed?.Invoke();
    }

    public void DismissRestartNotice()
    {
        var install = Current.Shell.Install;
        if (install is null) return;
        Save(s => s.WithRestartNotice(install.FlavorDir, owed: false));
        lock (_gate) _current = _current with { RestartNotice = false };
        Changed?.Invoke();
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
            _generation++;
            watcher = _watcher;
            _watcher = null;
            _current = _current with { Shell = _current.Shell with { Stopping = true } };
        }
        watcher?.Dispose();
        Changed?.Invoke();
        return Task.Run(() =>
        {
            lock (_passGate) { }
            _log?.Write("stopped");
        });
    }

    private void Use(WowInstall? install, string? problem)
    {
        Watcher? old;
        Watcher? watcher = null;
        lock (_gate)
        {
            if (_stopping) return;
            var generation = ++_generation;
            old = _watcher;
            _pass = null;
            if (install is not null)
            {
                _pass = NewPass(install, _current.KeepScreenshots);
                // The generation is captured: a callback of THIS watcher that arrives after the
                // install has changed again finds a newer number and does nothing.
                watcher = Watcher.For(install, () => RunPass(generation), Failed);
                watcher.Paused = _current.Shell.Paused;
            }
            _watcher = watcher;
            _current = _current with
            {
                RestartNotice = RestartNoticeDue(install),
                Shell = _current.Shell with
                {
                    Install = install,
                    InstallProblem = problem,
                    Report = null,
                    LastError = null,
                },
            };
        }
        old?.Dispose();
        Changed?.Invoke();
        if (watcher is not null && !Current.Shell.Paused) watcher.RunNow();
    }

    private ConvertPass NewPass(WowInstall install, bool keep) =>
        new(install, new ConvertOptions(KeepScreenshots: keep), m => _log?.Write(m));

    private void RunPass(int generation)
    {
        lock (_passGate)
        {
            ConvertPass pass;
            WowInstall install;
            lock (_gate)
            {
                if (_stopping || generation != _generation || _pass is null || _current.Shell.Install is null) return;
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
                current = generation == _generation;
                _current = _current with { Shell = _current.Shell with { Converting = false } };
                if (current)
                {
                    _current = _current with
                    {
                        RestartNotice = RestartNoticeDue(install),
                        Shell = _current.Shell with
                        {
                            Report = report ?? _current.Shell.Report,
                            LastError = error,
                        },
                    };
                }
            }
            Changed?.Invoke();
            if (current && report is not null) PassCompleted?.Invoke(report);
        }
    }

    // The watcher caught something RunPass did not: only a subscriber can have thrown it.
    private void Failed(Exception ex) => _log?.Write($"after the pass: {ex}");

    /// <summary>
    /// Written down BEFORE the pass that may create the addon, so the notice is owed even if
    /// the app is killed the moment after. An addon that is already there is never owed one.
    /// </summary>
    private void OweRestartNotice(WowInstall install)
    {
        if (File.Exists(new CutoutFolder(install.CutoutAddonDir).TocPath)) return;
        if (_settings.OwesRestartNotice(install.FlavorDir)) return;
        Save(s => s.WithRestartNotice(install.FlavorDir, owed: true));
    }

    private bool RestartNoticeDue(WowInstall? install) =>
        install is not null
        && _settings.OwesRestartNotice(install.FlavorDir)
        && File.Exists(new CutoutFolder(install.CutoutAddonDir).TocPath);

    private void Save(Func<Settings, Settings> change)
    {
        string? problem = null;
        lock (_gate)
        {
            _settings = change(_settings);
            try
            {
                _settings.Save(_dataDir);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                problem = $"Settings could not be saved: {ex.Message}";
            }
            _current = _current with { SettingsProblem = problem };
        }
        if (problem is not null) _log?.Write(problem);
    }

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
    }
}
