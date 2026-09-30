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

            _controller = new Controller(Options);
            // Subscribed BEFORE Start: the first pass begins inside it.
            _controller.Changed += QueueRefresh;
            _controller.PassCompleted += report => Dispatcher.UIThread.Post(() => Announce(report));
            _controller.Start();
            _viewModel = new MainViewModel(_controller);

            using (var ico = AssetLoader.Open(new Uri("avares://AltStableCompanion/Assets/tray.ico")))
            using (var bytes = new MemoryStream())
            {
                ico.CopyTo(bytes);
                _tray = new Win32TrayIcon(bytes.ToArray(), Tip(), Menu);
            }
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
                _viewModel?.Refresh();
                _tray?.SetTip(Tip());
            }, DispatcherPriority.Background);
        });
    }

    private string Tip() => PassText.TrayTip(_controller?.Current.Shell ?? new ShellState());

    private void Announce(PassReport report)
    {
        if (_quitting) return;
        if (PassText.Balloon(report) is not { } balloon) return;
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
        _viewModel.Refresh();
        _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
        return true;
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
        if (_window is not null) _window.Quitting = true;
        _tray?.Dispose();
        _tray = null;
        _controller?.Dispose();
        Instance?.Dispose();
    }
}
