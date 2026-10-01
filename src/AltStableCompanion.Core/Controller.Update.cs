namespace AltStableCompanion.Core;

public sealed partial class Controller
{
    /// <summary>
    /// The window is open again: how often an automatic check may run. GitHub allows sixty
    /// unauthenticated calls an hour from one address; a window opened and closed all evening
    /// is one of them.
    /// </summary>
    public static readonly TimeSpan CheckAgainAfter = TimeSpan.FromHours(1);

    private Updater? _updater;                               // made in Start; none without hooks
    private DateTime? _autoChecked;                          // the clock's time of the last automatic check

    /// <summary>The version the app runs, as stamped by the build.</summary>
    public string Version => update?.Version ?? "";

    /// <summary>The player pressed "Check for updates".</summary>
    public void CheckForUpdates()
    {
        if (_updater is null) return;
        Note("update: check asked for");
        _ = _updater.CheckAsync();
    }

    /// <summary>The player pressed "Update now": download the release found, and check it.</summary>
    public void DownloadUpdate()
    {
        if (_updater is null) return;
        _ = _updater.DownloadAsync();
    }

    /// <summary>
    /// The window was shown. With the setting on, this is when a check runs by itself - at most
    /// once an hour, and never over a download or an exe already waiting.
    /// </summary>
    public void WindowOpened()
    {
        if (_updater is null) return;
        bool check;
        lock (_gate)
        {
            var now = _clock();
            check = _settings.CheckUpdatesOnOpen && !_stopping
                && (_autoChecked is null || now - _autoChecked.Value >= CheckAgainAfter)
                && _updater.State.Stage is UpdateStage.None or UpdateStage.UpToDate or UpdateStage.Available or UpdateStage.Failed;
            if (check) _autoChecked = now;
        }
        if (!check) return;
        Note("update: checking, the window was opened");
        _ = _updater.CheckAsync();
    }

    public void SetCheckUpdatesOnOpen(bool on)
    {
        lock (_gate)
        {
            if (_current.CheckUpdatesOnOpen == on) return;
            _current = _current with { CheckUpdatesOnOpen = on };
        }
        Save(s => s with { CheckUpdatesOnOpen = on });
    }

    /// <summary>
    /// After the controller has stopped: put the downloaded exe in place of the running one.
    /// The path to start, or null with the reason when nothing was ready or the swap failed -
    /// in which case the running exe is still where it was.
    /// </summary>
    public (string? Exe, string? Problem) InstallUpdate()
    {
        if (_updater is null || update?.ExePath is not { } exe) return (null, "the running exe's path is not known");
        var state = _updater.State;
        if (state.Stage != UpdateStage.Ready || state.File is null) return (null, "no update is ready");
        try
        {
            var started = Updater.Install(exe, state.File);
            Note($"update: {state.Release?.Version.Text} put in place of {exe}");
            return (started, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Note($"update: could not put {state.File} in place: {ex.Message}");
            return (null, ex.Message);
        }
    }

    private void StartUpdater()
    {
        if (update is null) return;
        Updater.CleanUp(update.ExePath);
        var updater = new Updater(update, m => _log?.Write(m), _clock);
        updater.Changed += () =>
        {
            lock (_gate) _current = _current with { Update = updater.State };
            Changed?.Invoke();
        };
        _updater = updater;
    }

    private void StopUpdater() => _updater?.Cancel();
}
