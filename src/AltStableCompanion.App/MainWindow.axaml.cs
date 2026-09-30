using System.ComponentModel;
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

    private string _skin = Skins.Clear;
    private SkinPalette? _worn;

    public MainWindow()
    {
        InitializeComponent();
        // What Windows blends the window with where blur is not to be had: dark, never the
        // default white. Set before the window is ever shown.
        TransparencyBackgroundFallback = new SolidColorBrush(SkinPalette.Flat.Body);
        // Readability does not wait for a notification, but when one comes the look follows.
        PropertyChanged += (_, e) =>
        {
            if (e.Property == ActualTransparencyLevelProperty) Wear();
        };
        DataContextChanged += (_, _) =>
        {
            if (DataContext is MainViewModel vm)
            {
                vm.PropertyChanged += OnViewModelChanged;
                Skin(vm.Skin);
            }
        };
    }

    /// <summary>Set when the app is going: the close is then a close.</summary>
    public bool Quitting { get; set; }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        if (this.GetPlatformSettings() is { } settings) settings.ColorValuesChanged += (_, _) => Wear();
        Wear();
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.Skin) && sender is MainViewModel vm) Skin(vm.Skin);
    }

    private void Skin(string skin)
    {
        _skin = Skins.Normalize(skin);
        TransparencyLevelHint = SkinPalette.Of(_skin).Glass && !HighContrast() ? WantGlass : WantNone;
        Wear();
    }

    /// <summary>
    /// Put on the skin that can actually be worn now. A glass skin is worn as glass only when
    /// the platform gave the window a blur to show through; with Windows' transparency turned
    /// off, in a remote session, or under high contrast, the same skin is worn opaque. The
    /// choice is kept; only the look follows what is there.
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
        Background = palette.Glass ? Brushes.Transparent : new SolidColorBrush(palette.Body);
    }

    private bool HighContrast()
    {
        try
        {
            return this.GetPlatformSettings()?.GetColorValues().ContrastPreference == ColorContrastPreference.High;
        }
        catch (Exception)
        {
            return false;
        }
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
