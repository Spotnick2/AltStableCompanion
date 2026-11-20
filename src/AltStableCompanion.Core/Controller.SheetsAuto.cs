using System.Diagnostics;

namespace AltStableCompanion.Core;

/// <summary>
/// Whether the game of an install is running: a <c>Wow*.exe</c> process (how a flavour folder is
/// recognised, <see cref="WowInstall"/>) whose executable is in its folder - either account's
/// client. One whose path cannot be read counts as running: an export never starts in the
/// middle of a session because a process could not be asked where it lives.
/// </summary>
public static class WowProcess
{
    public static bool Running(WowInstall install)
    {
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                if (!p.ProcessName.StartsWith("Wow", StringComparison.OrdinalIgnoreCase)) continue;
                string? exe;
                try { exe = p.MainModule?.FileName; }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException)
                {
                    return true;
                }
                if (exe is null) return true;
                if (string.Equals(Path.GetDirectoryName(exe), Path.GetFullPath(install.FlavorDir).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        return false;
    }
}

/// <summary>
/// The automatic export (#82, M2): <see cref="Settings.SheetAutoMinutes"/> after the game has
/// closed and stayed closed - the owner swaps between two accounts' clients, so a write of the
/// saved file is not the end of a session (2026-11-19) - when an account's
/// <c>AltStable.lua</c> was written since the last export. Each tick:
/// <list type="bullet">
/// <item>the game running: noted, nothing else;</item>
/// <item>closed: once the newest save, and the last time the game was seen, are both that long
///   ago - the game writes the file as it closes, so a start of the app after a session counts
///   too - one export, as Export now would. A failed one waits that long again.</item>
/// </list>
/// Not while paused, disconnected, or with the setting off. Times are UTC: a save after the
/// autumn clock change is not "older" than one before it (Codex on #87).
/// </summary>
public sealed partial class Controller
{
    private Timer? _sheetsTimer;
    private DateTime? _gameSeenAt;          // under _gate, UTC: the last tick that saw the game running
    private DateTime _autoRetryAfter;       // under _gate, UTC: a failed automatic export waits until then
    private int _ticking;

    // What a tick saw when it found an export due: the install it looked at and its newest save.
    private sealed record AutoClaim(WowInstall Install, DateTime Newest);

    private DateTime UtcNow() => _clock().ToUniversalTime();

    /// <summary>A tick's findings handed straight to the start, as if the player acted in between: tests.</summary>
    internal void StartAutoForTest(DateTime newestUtc)
    {
        if (Current.Shell.Install is { } install) StartExport(new AutoClaim(install, newestUtc));
    }

    // Under _gate: the tick's findings still hold - the same install, the setting on, not paused,
    // not held after a failure, and the save it saw still newer than the last export.
    private bool StillDue(AutoClaim claim) =>
        _settings.SheetAutoExport && !_current.Shell.Paused && UtcNow() >= _autoRetryAfter
        && _current.Shell.Install is { } now
        && string.Equals(now.FlavorDir, claim.Install.FlavorDir, StringComparison.OrdinalIgnoreCase)
        && (_settings.SheetExportedThrough is not { } done || claim.Newest > done);

    private void StartSheetsTimer(SheetHooks hooks) =>
        _sheetsTimer = new Timer(_ => SheetTick(), null, hooks.Tick, hooks.Tick);

    private void StopSheetsTimer() => _sheetsTimer?.Dispose();

    // On a timer thread: nothing leaves it, and one tick at a time.
    private void SheetTick()
    {
        if (Interlocked.Exchange(ref _ticking, 1) == 1) return;
        try { TickOnce(); }
        catch (Exception ex) { _log?.Write($"google: the automatic export's check failed: {ex.GetType().Name}: {ex.Message}"); }
        finally { Interlocked.Exchange(ref _ticking, 0); }
    }

    private void TickOnce()
    {
        if (sheets is not { } hooks) return;
        WowInstall install;
        int minutes;
        DateTime? through;
        lock (_gate)
        {
            if (_stopping || _current.Shell.Install is not { } i) return;
            (install, minutes, through) = (i, _settings.SheetAutoMinutes, _settings.SheetExportedThrough);
        }
        var now = UtcNow();
        bool running;
        try { running = hooks.GameRunning(install); }
        catch (Exception) { running = true; }
        DateTime? seen;
        lock (_gate)
        {
            if (running) _gameSeenAt = now;
            if (running || !_settings.SheetAutoExport || _current.Shell.Paused
                || _current.Sheets is not { Google: GoogleState.Connected, Exporting: false } || now < _autoRetryAfter) return;
            seen = _gameSeenAt;
        }
        if (NewestSave(install) is not { } newest || (through is { } done && newest <= done)) return;
        var quietSince = seen is { } s && s > newest ? s : newest;
        if (now - quietSince < TimeSpan.FromMinutes(minutes)) return;
        _log?.Write($"google: exporting on its own, {minutes} minutes after the game closed");
        StartExport(new AutoClaim(install, newest));
    }

    /// <summary>When an account's <c>AltStable.lua</c> was last written, UTC, or null for none.</summary>
    internal static DateTime? NewestSave(WowInstall install)
    {
        DateTime? newest = null;
        foreach (var path in Leftovers.AccountFiles(install.AccountsDir))
        {
            if (!File.Exists(path)) continue;
            var at = File.GetLastWriteTimeUtc(path);
            if (newest is not { } n || at > n) newest = at;
        }
        return newest;
    }

    /// <summary>"Export on its own, N minutes after the game closes": the box and the number.</summary>
    public void SetSheetAuto(bool on, int minutes)
    {
        minutes = Math.Clamp(minutes, Settings.MinSheetAutoMinutes, Settings.MaxSheetAutoMinutes);
        lock (_gate)
        {
            _current = _current with { Sheets = _current.Sheets with { Auto = on, AutoMinutes = minutes } };
            // A setting changed: a held failure does not hold the next try.
            _autoRetryAfter = default;
        }
        Save(s => s with { SheetAutoExport = on, SheetAutoMinutes = minutes });
    }

    // The saves an export read, kept - while its session is the current one: the next
    // automatic export waits for a newer one.
    private void SaveExported(DateTime? through, int session)
    {
        if (through is null) return;
        lock (_gate)
        {
            if (session != _googleSession) return;
            _settings = _settings with { SheetExportedThrough = through };
            Write();
        }
    }

    // An automatic export that failed: not again for one more wait.
    private void HoldAuto()
    {
        lock (_gate) _autoRetryAfter = UtcNow() + TimeSpan.FromMinutes(_settings.SheetAutoMinutes);
    }
}
