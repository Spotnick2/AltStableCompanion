using AltStableCompanion.App.Platform;

namespace AltStableCompanion.App;

/// <summary>
/// One converter per session. Two would each run passes over the same screenshots and write
/// the same cutouts.
///
/// The first instance owns a named mutex; a later one sets a named event, which the first is
/// waiting on, and leaves. The event is created BEFORE the mutex is tried, and is auto-reset,
/// so a signal sent while the first instance is still starting waits for it.
///
/// "Local\" is this logon session. An instance running under other privileges (elevated, when
/// this one is not) owns objects this one may not open: that reads as <see cref="Blocked"/>,
/// and never as permission to start a second converter.
/// </summary>
internal sealed class SingleInstance : IDisposable
{
    private const string MutexName = @"Local\AltStableCompanion";
    private const string EventName = @"Local\AltStableCompanion.Show";
    private const string ShownName = @"Local\AltStableCompanion.Shown";

    private readonly EventWaitHandle? _show;
    private readonly EventWaitHandle? _shown;
    private readonly Mutex? _mutex;
    private RegisteredWaitHandle? _wait;
    private bool _disposed;

    /// <summary>This is the instance that runs.</summary>
    public bool First { get; }

    /// <summary>Another instance is there and cannot be reached.</summary>
    public bool Blocked { get; }

    public SingleInstance()
    {
        try
        {
            _show = new EventWaitHandle(false, EventResetMode.AutoReset, EventName);
            _shown = new EventWaitHandle(false, EventResetMode.AutoReset, ShownName);
            _mutex = new Mutex(false, MutexName);
            try
            {
                First = _mutex.WaitOne(0);
            }
            catch (AbandonedMutexException)
            {
                // The instance that held it died without letting go. It is ours now.
                First = true;
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or WaitHandleCannotBeOpenedException or IOException)
        {
            Blocked = true;
        }
    }

    /// <summary>
    /// Ask the first instance to show its window, and wait for it to say that it has. From the
    /// instance that is leaving. False when no answer came: the first instance is on its way
    /// out (it holds the mutex until its last pass is done), and telling the player that all
    /// is well would leave them with nothing running.
    /// </summary>
    public bool AskFirstToShow(TimeSpan wait)
    {
        if (_show is null || _shown is null) return false;
        // This process was started by the player and may take the foreground; hand that on,
        // or the first instance's window opens behind whatever is in front. Best effort.
        NativeMethods.AllowSetForegroundWindow(NativeMethods.ASFW_ANY);
        _shown.Reset();
        _show.Set();
        return _shown.WaitOne(wait);
    }

    /// <summary>The window is out: tell the instance that asked.</summary>
    public void Acknowledge()
    {
        if (!_disposed) _shown?.Set();
    }

    /// <summary>Call <paramref name="show"/> whenever a later instance asks. Once the UI exists.</summary>
    public void Listen(Action show)
    {
        if (!First || _show is null || _disposed) return;
        _wait = ThreadPool.RegisterWaitForSingleObject(_show, (_, _) =>
        {
            if (!_disposed) show();
        }, null, Timeout.Infinite, executeOnlyOnce: false);
    }

    /// <summary>On the thread that created it: a mutex is released by the thread that owns it.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _wait?.Unregister(null);
        if (First)
        {
            try
            {
                _mutex?.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // Not the owning thread. The handle closing with the process releases it.
            }
        }
        _mutex?.Dispose();
        _show?.Dispose();
        _shown?.Dispose();
    }
}
