namespace AltStableCompanion.Core;

/// <summary>
/// Runs a pass when something changes: a screenshot appears, or a client writes its capture
/// records (on reload or logout). Every trigger resets a short debounce, so the burst of events
/// from one capture - two screenshots, then the SavedVariables write and its .bak - makes one
/// pass. A slow poll backs the watchers up: FileSystemWatcher can overflow and drop events.
///
/// NO "seen" list, as in Update-Cutouts.ps1: a pair is only convertible once its record reaches
/// disk, so marking files seen on sight would skip exactly the capture just taken. Every pass is
/// a full, idempotent pass instead.
/// </summary>
public sealed class Watcher : IDisposable
{
    private readonly Action _pass;
    private readonly TimeSpan _debounce;
    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly Timer _debounceTimer;
    private readonly Timer _pollTimer;
    private readonly Lock _gate = new();
    private bool _running;
    private bool _again;
    private bool _paused;

    public Watcher(IEnumerable<(string Folder, string Filter, bool Recursive)> targets, Action pass,
        TimeSpan? debounce = null, TimeSpan? poll = null)
    {
        _pass = pass;
        _debounce = debounce ?? TimeSpan.FromSeconds(2);
        _debounceTimer = new Timer(_ => Run(), null, Timeout.Infinite, Timeout.Infinite);
        var every = poll ?? TimeSpan.FromSeconds(60);
        _pollTimer = new Timer(_ => Trigger(), null, every, every);

        foreach (var (folder, filter, recursive) in targets)
        {
            if (!Directory.Exists(folder)) continue;
            var w = new FileSystemWatcher(folder, filter)
            {
                IncludeSubdirectories = recursive,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
            };
            w.Created += (_, _) => Trigger();
            w.Changed += (_, _) => Trigger();
            w.Renamed += (_, _) => Trigger();
            w.Deleted += (_, _) => Trigger();
            w.Error += (_, _) => Trigger();              // buffer overflow: better one pass too many
            w.EnableRaisingEvents = true;
            _watchers.Add(w);
        }
    }

    /// <summary>The Screenshots folder and every account's AltStable.lua.</summary>
    public static Watcher For(WowInstall install, Action pass) => new(
        [
            (install.Screenshots, "*.tga", false),
            (install.AccountsDir, SavedVariablesReader.FileName, true),
        ], pass);

    public bool Paused
    {
        get { lock (_gate) return _paused; }
        set { lock (_gate) _paused = value; }
    }

    /// <summary>Something changed: run a pass once things have been quiet for the debounce.</summary>
    public void Trigger()
    {
        lock (_gate)
        {
            if (_paused) return;
        }
        _debounceTimer.Change(_debounce, Timeout.InfiniteTimeSpan);
    }

    // One pass at a time; a trigger during a pass runs one more after it.
    private void Run()
    {
        lock (_gate)
        {
            if (_running) { _again = true; return; }
            _running = true;
        }
        try
        {
            while (true)
            {
                _pass();
                lock (_gate)
                {
                    if (!_again) { _running = false; return; }
                    _again = false;
                }
            }
        }
        catch
        {
            lock (_gate) _running = false;
            throw;
        }
    }

    public void Dispose()
    {
        foreach (var w in _watchers) w.Dispose();
        _debounceTimer.Dispose();
        _pollTimer.Dispose();
    }
}
