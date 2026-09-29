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
///
/// A pass runs on a timer thread, where an exception nobody catches ends the process. So a
/// pass that throws is reported to <c>failed</c> and forgotten: the next trigger, or the poll,
/// runs a whole pass again.
/// </summary>
public sealed class Watcher : IDisposable
{
    private readonly Action _pass;
    private readonly Action<Exception>? _failed;
    private readonly TimeSpan _debounce;
    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly Timer _debounceTimer;
    private readonly Timer _pollTimer;
    private readonly Lock _gate = new();
    private bool _running;
    private bool _again;
    private bool _againAsked;
    private bool _paused;
    private bool _disposed;

    public Watcher(IEnumerable<(string Folder, string Filter, bool Recursive)> targets, Action pass,
        TimeSpan? debounce = null, TimeSpan? poll = null, Action<Exception>? failed = null)
    {
        _pass = pass;
        _failed = failed;
        _debounce = debounce ?? TimeSpan.FromSeconds(2);
        _debounceTimer = new Timer(_ => Run(asked: false), null, Timeout.Infinite, Timeout.Infinite);
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
    public static Watcher For(WowInstall install, Action pass, Action<Exception>? failed = null) => new(
        [
            (install.Screenshots, "*.tga", false),
            (install.AccountsDir, SavedVariablesReader.FileName, true),
        ], pass, failed: failed);

    /// <summary>
    /// Paused means nothing runs: not a new trigger, not a debounce that was already counting
    /// down, not the rerun queued behind a pass. A pass deletes screenshots; "pause" that let one
    /// more through would not be one. A pass already running finishes.
    /// </summary>
    public bool Paused
    {
        get { lock (_gate) return _paused; }
        set
        {
            lock (_gate)
            {
                var resumed = _paused && !value;
                _paused = value;
                if (value)
                {
                    _again = false;
                }
                else if (resumed && !_disposed)
                {
                    // Whatever happened during the pause was not looked at: look now.
                    _debounceTimer.Change(_debounce, Timeout.InfiniteTimeSpan);
                }
            }
        }
    }

    /// <summary>
    /// The player asked for a pass ("Convert now"): run one at once, paused or not. It does not
    /// go through the debounce - the next file event would push it back - and if a pass is
    /// running, one more follows it.
    /// </summary>
    public void RunNow()
    {
        lock (_gate)
        {
            if (_disposed) return;
        }
        ThreadPool.QueueUserWorkItem(_ => Run(asked: true));
    }

    /// <summary>Something changed: run a pass once things have been quiet for the debounce.</summary>
    public void Trigger()
    {
        lock (_gate)
        {
            if (_paused || _disposed) return;
            _debounceTimer.Change(_debounce, Timeout.InfiniteTimeSpan);
        }
    }

    // One pass at a time; a trigger during a pass runs one more after it. A pause stops what
    // the watcher started by itself, never what the player asked for.
    private void Run(bool asked)
    {
        lock (_gate)
        {
            if (_disposed || (_paused && !asked)) return;
            if (_running)
            {
                if (asked) _againAsked = true; else _again = true;
                return;
            }
            _running = true;
        }
        while (true)
        {
            try
            {
                _pass();
            }
            catch (Exception ex)
            {
                Report(ex);
            }
            lock (_gate)
            {
                var more = !_disposed && (_againAsked || (_again && !_paused));
                _again = false;
                _againAsked = false;
                if (!more)
                {
                    _running = false;
                    return;
                }
            }
        }
    }

    // Whoever is told must not be able to end the process either.
    private void Report(Exception ex)
    {
        try
        {
            _failed?.Invoke(ex);
        }
        catch (Exception)
        {
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _again = false;
            _againAsked = false;
        }
        foreach (var w in _watchers) w.Dispose();
        _debounceTimer.Dispose();
        _pollTimer.Dispose();
    }
}
