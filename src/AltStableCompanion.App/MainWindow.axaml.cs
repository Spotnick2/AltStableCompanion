using System.ComponentModel;
using System.Runtime.InteropServices;
using AltStableCompanion.App.Platform;
using AltStableCompanion.Core;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;

namespace AltStableCompanion.App;

internal sealed partial class MainWindow : Window
{
    private static readonly IReadOnlyList<WindowTransparencyLevel> WantGlass = [WindowTransparencyLevel.AcrylicBlur, WindowTransparencyLevel.Blur];
    private static readonly IReadOnlyList<WindowTransparencyLevel> WantNone = [WindowTransparencyLevel.None];

    private static readonly Geometry MaximizeGlyph = Geometry.Parse("M0.5,0.5 L9.5,0.5 L9.5,9.5 L0.5,9.5 Z");
    private static readonly Geometry RestoreGlyph = Geometry.Parse("M2.5,2.5 L2.5,0.5 L9.5,0.5 L9.5,7.5 L7.5,7.5 M0.5,2.5 L7.5,2.5 L7.5,9.5 L0.5,9.5 Z");

    // The window styles BorderOnly leaves out. Without WS_SYSMENU there is no Alt+Space and no
    // system menu on the icon; the snap-layout flyout on the maximise button needs the box
    // styles, which need WS_SYSMENU. WS_CAPTION is what makes Windows treat it as a real
    // window (Win+Up, the Aero shake), and the client area covers it anyway.
    private const uint WS_CAPTION = 0x00C00000;
    private const uint WS_SYSMENU = 0x00080000;
    private const uint WS_MINIMIZEBOX = 0x00020000;
    private const uint WS_MAXIMIZEBOX = 0x00010000;

    private readonly IPlatformSettings? _platform;
    private bool _docked;
    private PlatformColorValues? _colors;
    private MainViewModel? _vm;
    private string _skin = Skins.Clear;
    private SkinPalette? _worn;

    public MainWindow()
    {
        InitializeComponent();
        if (OperatingSystem.IsWindows())
        {
            Win32Properties.AddWindowStylesCallback(this, (style, exStyle) =>
                (style | WS_CAPTION | WS_SYSMENU | WS_MINIMIZEBOX | WS_MAXIMIZEBOX, exStyle));
        }
        // What Windows blends the window with where blur is not to be had: dark, never the
        // default white. Set before the window is ever shown.
        TransparencyBackgroundFallback = new SolidColorBrush(SkinPalette.Flat.Body);
        // The strip shows what the taskbar shows: the window's own icon and title.
        StripIcon.Source = IconImage();
        StripTitle.Text = Title;
        // Readability does not wait for a notification, but when one comes the look follows.
        // The platform settings are process-wide: one subscription, released with the window.
        _platform = this.GetPlatformSettings();
        if (_platform is not null)
        {
            _colors = ColorsOf(_platform);
            _platform.ColorValuesChanged += OnColorValuesChanged;
        }
        PropertyChanged += (_, e) =>
        {
            if (e.Property == ActualTransparencyLevelProperty) Wear();
            if (e.Property == WindowStateProperty) FollowState();
        };
        DataContextChanged += (_, _) =>
        {
            if (_vm is not null) _vm.PropertyChanged -= OnViewModelChanged;
            _vm = DataContext as MainViewModel;
            if (_vm is not null)
            {
                _vm.PropertyChanged += OnViewModelChanged;
                Skin(_vm.Skin);
            }
        };
        FollowState();
        // Placed before it is shown, so it does not appear elsewhere and hop; confirmed once open.
        DockByTray(shown: false);
    }

    /// <summary>Set when the app is going: the close is then a close.</summary>
    public bool Quitting { get; set; }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        Wear();
        // Now the frame is known: the first time, to correct the estimate; every time, when
        // the window would come back somewhere no screen is any more.
        if (!_docked || !OnAScreen()) DockByTray(shown: true);
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_platform is not null) _platform.ColorValuesChanged -= OnColorValuesChanged;
        if (_vm is not null) _vm.PropertyChanged -= OnViewModelChanged;
        _vm = null;
        base.OnClosed(e);
    }

    private void OnColorValuesChanged(object? sender, PlatformColorValues values)
    {
        _colors = values;
        Wear();
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.Skin) && sender is MainViewModel vm) Skin(vm.Skin);
    }

    // The skin is one of Skins.All by the time it gets here: the controller saw to it.
    private void Skin(string skin)
    {
        _skin = skin;
        // The hint follows the skin alone. Whether glass is worn today is decided in Wear, so a
        // change in the platform's answer - high contrast on or off - changes the look, not the choice.
        TransparencyLevelHint = SkinPalette.Of(_skin).Glass ? WantGlass : WantNone;
        Wear();
    }

    /// <summary>
    /// Put on the skin that can actually be worn now. A glass skin is worn as glass only when
    /// the platform gave the window a blur to show through; with Windows' transparency turned
    /// off, in a remote session, or under high contrast, the same skin is worn opaque. The
    /// choice is kept; only the look follows what is there. The window's own background is
    /// the body brush: translucent over the blur, solid when there is none.
    /// </summary>
    private void Wear()
    {
        var chosen = SkinPalette.Of(_skin);
        var blurred = ActualTransparencyLevel == WindowTransparencyLevel.AcrylicBlur
            || ActualTransparencyLevel == WindowTransparencyLevel.Blur;
        var palette = chosen.Glass && blurred && !HighContrast() ? chosen : chosen.Opaque();
        if (palette == _worn) return;
        _worn = palette;
        Resources["BodyBrush"] = new SolidColorBrush(palette.Body);
        Resources["PaneBrush"] = new SolidColorBrush(palette.Pane);
        Resources["DataBrush"] = new SolidColorBrush(palette.Data);
        Resources["RimBrush"] = new SolidColorBrush(palette.Rim);
    }

    private bool HighContrast() => _colors?.ContrastPreference == ColorContrastPreference.High;

    private static PlatformColorValues? ColorsOf(IPlatformSettings platform)
    {
        try
        {
            return platform.GetColorValues();
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or NotSupportedException)
        {
            // WinRT not to be had (an old build, a server SKU): no high contrast to speak of.
            return null;
        }
    }

    // The window's icon, as an image for the strip: one file, read once.
    private IImage? IconImage()
    {
        if (Icon is null) return null;
        var bytes = new MemoryStream();
        Icon.Save(bytes);
        bytes.Position = 0;
        return new Avalonia.Media.Imaging.Bitmap(bytes);
    }

    // A tray app's window opens by the tray: the corner of the primary screen's working area
    // that the taskbar is on - bottom-right, unless the taskbar is on the top or the left -
    // and no taller than that area. Position is the frame's top-left, before and after the
    // show alike (measured); before the show the frame's size is not known, so its borders
    // come from the system metrics, and once shown, from the frame itself. After the first
    // show the window keeps wherever the player put it: close-to-tray hides it, and Show
    // brings it back there - unless there is no screen there any more.
    private void DockByTray(bool shown)
    {
        if (Screens.Primary is not { } screen) return;
        var area = screen.WorkingArea;
        var scale = shown ? RenderScaling : screen.Scaling;
        var dpi = (uint)Math.Round(96 * scale);
        var margin = (int)Math.Round(12 * scale);

        // The frame's excess over the client: two side borders across, one below (the client
        // reaches the top).
        double excessW, excessH;
        if (shown && FrameSize is { } frame)
        {
            (excessW, excessH) = (frame.Width - ClientSize.Width, frame.Height - ClientSize.Height);
        }
        else
        {
            var padded = NativeMethods.GetSystemMetricsForDpi(NativeMethods.SM_CXPADDEDBORDER, dpi);
            excessW = 2 * (NativeMethods.GetSystemMetricsForDpi(NativeMethods.SM_CXFRAME, dpi) + padded) / scale;
            excessH = (NativeMethods.GetSystemMetricsForDpi(NativeMethods.SM_CYFRAME, dpi) + padded) / scale;
        }

        // No taller than the working area, margins included: a laptop at 150% has less than
        // this window's default height, and a small one less than its minimum - which then
        // gives way too, down to what still shows the headline. On screen beats complete.
        var room = Math.Floor(area.Height / scale - 2 * margin / scale - excessH);
        if (Height > room)
        {
            var fit = Math.Max(240, room);
            MinHeight = Math.Min(MinHeight, fit);
            Height = fit;
        }

        var w = (int)Math.Round((Width + excessW) * scale);
        var h = (int)Math.Round((Height + excessH) * scale);
        var (left, top) = TaskbarEdge();
        var x = left ? area.X + margin : area.Right - w - margin;
        var y = top ? area.Y + margin : area.Bottom - h - margin;
        Position = new PixelPoint(Math.Max(area.X, x), Math.Max(area.Y, y));
        _docked = shown;
    }

    // Which edge the taskbar is on: (left, top). Anything else, or no answer, is the
    // bottom-right corner.
    private static (bool Left, bool Top) TaskbarEdge()
    {
        var data = new NativeMethods.APPBARDATA { cbSize = (uint)Marshal.SizeOf<NativeMethods.APPBARDATA>() };
        if (NativeMethods.SHAppBarMessage(NativeMethods.ABM_GETTASKBARPOS, ref data) == 0) return (false, false);
        return (data.uEdge == NativeMethods.ABE_LEFT, data.uEdge == NativeMethods.ABE_TOP);
    }

    // Whether the window's top-left, a little way in, is on some screen: a monitor that was
    // unplugged since the window was last seen leaves it nowhere.
    private bool OnAScreen()
    {
        var probe = new PixelPoint(Position.X + 40, Position.Y + 20);
        return Screens.All.Any(s => s.Bounds.Contains(probe));
    }

    // The maximise button says what it will do next.
    private void FollowState()
    {
        var maximized = WindowState == WindowState.Maximized;
        MaximizeGlyphPath.Data = maximized ? RestoreGlyph : MaximizeGlyph;
        ToolTip.SetTip(MaximizeButton, maximized ? "Restore" : "Maximise");
    }

    // Closing the window leaves the app in the tray. Only the player's own close is turned
    // into that: Windows shutting down, or the app quitting, has to get its close.
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (!Quitting && e.CloseReason == WindowCloseReason.WindowClosing)
        {
            e.Cancel = true;
            Hide();
        }
        base.OnClosing(e);
    }

    // Enter in the level box applies it, as leaving the box does. The box is then told what
    // the level is, by hand: a binding does not re-publish a value it already published, and
    // "abc" must go back to "15" although "15" is what the box was last told.
    private void OnLevelKeyDown(object? sender, Avalonia.Input.KeyEventArgs e)
    {
        if (e.Key != Avalonia.Input.Key.Enter) return;
        ApplyLevel(sender);
        e.Handled = true;
    }

    private void OnLevelLostFocus(object? sender, RoutedEventArgs e) => ApplyLevel(sender);

    private void ApplyLevel(object? sender)
    {
        if (sender is not TextBox box || DataContext is not MainViewModel vm) return;
        vm.EnhanceMinLevel = box.Text ?? "";
        box.Text = vm.EnhanceMinLevel;
    }

    private void OnMinimize(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximize(object? sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    private async void OnBrowse(object? sender, RoutedEventArgs e)
    {
        try
        {
            var picked = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "The World of Warcraft folder, or _classic_beta_ inside it",
                AllowMultiple = false,
            });
            if (picked.Count > 0 && picked[0].TryGetLocalPath() is { } folder && DataContext is MainViewModel vm)
            {
                vm.Picked(folder);
            }
        }
        catch (Exception)
        {
            // An async void handler: nothing above it to catch what the picker throws.
        }
    }
}
