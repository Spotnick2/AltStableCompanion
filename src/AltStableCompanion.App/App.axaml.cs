using AltStableCompanion.App.Platform;
using AltStableCompanion.Core;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Threading;

namespace AltStableCompanion.App;

internal sealed partial class App : Application
{
    /// <summary>Set by Main before Avalonia starts: the command line, and the instance lock.</summary>
    public static StartupOptions Options { get; set; } = new();
    public static SingleInstance? Instance { get; set; }

    private Controller? _controller;
    private MainViewModel? _viewModel;
    private MainWindow? _window;
    private Win32TrayIcon? _tray;
    private IClassicDesktopStyleApplicationLifetime? _desktop;
    private bool _refreshQueued;
    private bool _quitting;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _desktop = desktop;
            // Windows logging off or shutting down: no time to wait for a pass, only to tidy.
            desktop.ShutdownRequested += (_, _) => Leave();
            desktop.Exit += (_, _) => Leave();

            // The real way to Codex: the CLI on PATH, its own home, its own sign-in. The
            // controller never uses it unless the player turned enhancement on. The real way
            // to GitHub: this exe, its stamped version, and the network - used on a press.
            _controller = new Controller(Options, enhance: EnhanceHooks.Real(), update: UpdateHooks.Real());
            // Subscribed BEFORE Start: the first pass begins inside it.
            _controller.Changed += QueueRefresh;
            _controller.PassCompleted += report => Dispatcher.UIThread.Post(() => Announce(report));
            _controller.EnhanceCompleted += result => Dispatcher.UIThread.Post(() => Announce(result));
            _controller.Start();
            _viewModel = new MainViewModel(_controller);
            _viewModel.RestartRequested += Restart;

            _icon = Asset("avares://AltStableCompanion/Assets/tray.ico");
            _busyIcon = Asset("avares://AltStableCompanion/Assets/tray-busy.ico");
            _tray = new Win32TrayIcon(_icon, Tip(), Menu);
            _tray.Activated += () => ShowWindow();
            _controller.Note(_tray.Present ? "tray icon added" : "tray icon could not be added - trying again");
            // No icon means no way in: a window that started hidden has to come out.
            _tray.PresenceChanged += present =>
            {
                _controller.Note(present ? "tray icon added" : "tray icon lost - trying again");
                if (!present) ShowWindow();
            };
            // The process ending by another road: take the icon out, touch nothing else.
            AppDomain.CurrentDomain.ProcessExit += (_, _) => _tray?.RemoveIcon();

            // The instance that asked is waiting to hear that the window is out. It hears
            // nothing when this one is on its way out, and tells the player so.
            Instance?.Listen(() => Dispatcher.UIThread.Post(() =>
            {
                if (ShowWindow()) Instance?.Acknowledge();
            }));

            if (!Options.Minimized || !_tray.Present) ShowWindow();
        }
        base.OnFrameworkInitializationCompleted();
    }

    // Many changes, one refresh: a pass raises several in a row, from its own thread.
    private void QueueRefresh()
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_refreshQueued) return;
            _refreshQueued = true;
            Dispatcher.UIThread.Post(() =>
            {
                _refreshQueued = false;
                if (_quitting) return;
                _viewModel?.Refresh();
                _tray?.SetTip(Tip());
                // The shield wears a dot while a picture is being made: a glance at the tray says so.
                _tray?.SetIcon(_controller?.Current.Shell.Enhancing is null ? _icon! : _busyIcon!);
            }, DispatcherPriority.Background);
        });
    }

    private byte[]? _icon;
    private byte[]? _busyIcon;

    private static byte[] Asset(string uri)
    {
        using var stream = AssetLoader.Open(new Uri(uri));
        using var bytes = new MemoryStream();
        stream.CopyTo(bytes);
        return bytes.ToArray();
    }

    private string Tip() => PassText.TrayTip(_controller?.Current.Shell ?? new ShellState());

    private void Announce(EnhanceResult result)
    {
        if (_quitting) return;
        if (PassText.EnhanceBalloon(result, _controller?.Current.RestartNotice ?? false) is not { } balloon) return;
        var taken = _tray?.ShowBalloon(balloon.Title, balloon.Text) ?? false;
        _controller?.Note(taken
            ? $"balloon handed to Windows: {balloon.Title}"
            : $"no balloon, there is no tray icon: {balloon.Title}");
    }

    private void Announce(PassReport report)
    {
        if (_quitting) return;
        if (PassText.Balloon(report, _controller?.Current.RestartNotice ?? false) is not { } balloon) return;
        var taken = _tray?.ShowBalloon(balloon.Title, balloon.Text) ?? false;
        _controller?.Note(taken
            ? $"balloon handed to Windows: {balloon.Title}"
            : $"no balloon, there is no tray icon: {balloon.Title}");
    }

    private IReadOnlyList<TrayMenuItem> Menu()
    {
        var now = _controller!.Current;
        var usable = now.Shell.Install is not null && !now.Shell.Stopping;
        return
        [
            new TrayMenuItem("Open", () => ShowWindow()),
            new TrayMenuItem("Check for new captures", _controller.ConvertNow, Enabled: usable && !now.Shell.FirstStart),
            new TrayMenuItem("Process new captures automatically", () => _controller.SetPaused(!_controller.Current.Shell.Paused),
                Checked: !now.Shell.Paused),
            new TrayMenuItem("Open Cutouts folder", () => _viewModel?.OpenCutouts.Execute(null), Enabled: usable),
            new TrayMenuItem(null),
            new TrayMenuItem("Quit", Quit),
        ];
    }

    private bool ShowWindow()
    {
        if (_quitting || _viewModel is null) return false;
        if (_window is null)
        {
            _window = new MainWindow { DataContext = _viewModel };
            _window.Closed += (_, _) => _window = null;
        }
        _viewModel.WantThumbnails = true;
        _viewModel.Refresh();
        _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
        // With the setting on, the window opening is when a check runs.
        _controller?.WindowOpened();
        return true;
    }

    // "Restart now" with an update ready: the same leaving as Quit - the pass in hand is
    // waited for - then the new exe goes in place of this one and is started. When the swap
    // fails the running file is still there, so that is what starts; the log says why.
    private async void Restart()
    {
        if (_quitting || _controller is null) return;
        _quitting = true;
        string? exe = null;
        string? problem = null;
        try
        {
            if (_window is not null)
            {
                _window.Quitting = true;
                _window.Close();
            }
            await _controller.StopAsync();
            (exe, problem) = _controller.InstallUpdate();
        }
        catch (Exception ex)
        {
            problem ??= ex.Message;
        }
        // The lock goes with Leave: the instance started next must find it free.
        Leave();
        var start = exe ?? Environment.ProcessPath;
        if (problem is not null)
        {
            NativeMethods.MessageBoxW(0, "The update could not be put in place: " + problem
                + "\n\nThe version you had is starting again.", "AltStable Companion", NativeMethods.MB_ICONERROR);
        }
        if (start is not null)
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo(start) { UseShellExecute = false };
                if (Options.WowDir is not null) { psi.ArgumentList.Add("--wow-dir"); psi.ArgumentList.Add(Options.WowDir); }
                if (Options.DataDir is not null) { psi.ArgumentList.Add("--data-dir"); psi.ArgumentList.Add(Options.DataDir); }
                System.Diagnostics.Process.Start(psi)?.Dispose();
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
            {
                NativeMethods.MessageBoxW(0, $"AltStable Companion could not be started again: {ex.Message}\n\nStart it yourself: {start}",
                    "AltStable Companion", NativeMethods.MB_ICONERROR);
            }
        }
        _desktop?.Shutdown();
    }

    // Quit waits for the pass in hand - a pass cut short has written cutouts its manifest
    // does not list - but the waiting is not done on this thread.
    private async void Quit()
    {
        if (_quitting) return;
        _quitting = true;
        try
        {
            if (_window is not null)
            {
                _window.Quitting = true;
                _window.Close();
            }
            if (_controller is not null) await _controller.StopAsync();
        }
        catch (Exception)
        {
            // Going anyway.
        }
        Leave();
        _desktop?.Shutdown();
    }

    private void Leave()
    {
        _quitting = true;
        // The window goes before the pictures it shows do.
        if (_window is not null)
        {
            _window.Quitting = true;
            _window.Close();
            _window = null;
        }
        _tray?.Dispose();
        _tray = null;
        _viewModel?.Dispose();
        _viewModel = null;
        _controller?.Dispose();
        Instance?.Dispose();
    }
}
