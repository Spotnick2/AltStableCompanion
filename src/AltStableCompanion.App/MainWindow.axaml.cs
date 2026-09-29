using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;

namespace AltStableCompanion.App;

internal sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    /// <summary>Set when the app is going: the close is then a close.</summary>
    public bool Quitting { get; set; }

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
