using System.Runtime.Versioning;
using AltStableCompanion.App.Platform;
using AltStableCompanion.Core;
using Avalonia;
using Avalonia.Controls;

[assembly: SupportedOSPlatform("windows")]

namespace AltStableCompanion.App;

internal static class Program
{
    private const string Title = "AltStable Companion";

    [STAThread]
    public static int Main(string[] args)
    {
        // A command line that is wrong stops the app here. It is never read as "no options":
        // that is how a mistyped --wow-dir would end up converting in the real install.
        var (options, error) = StartupOptions.Parse(args);
        if (options is null)
        {
            NativeMethods.MessageBoxW(0, error + "\n\nNothing was started.", Title, NativeMethods.MB_ICONERROR);
            return 2;
        }

        var instance = new SingleInstance();
        if (instance.Blocked)
        {
            NativeMethods.MessageBoxW(0,
                "AltStable Companion seems to be running already, under another account or as administrator.\n\n"
                + "Close that one first. Nothing was started.", Title, NativeMethods.MB_ICONERROR);
            return 3;
        }
        if (!instance.First)
        {
            if (options.Explicit)
            {
                // Its folders would be ignored by the instance that is running. Say so, rather
                // than open that one's window as if they had been used.
                NativeMethods.MessageBoxW(0,
                    "AltStable Companion is already running, so --wow-dir and --data-dir were not used.\n\n"
                    + "Quit it from its tray icon, then start this again.", Title, NativeMethods.MB_ICONERROR);
                instance.Dispose();
                return 1;
            }
            instance.AskFirstToShow();
            instance.Dispose();
            return 0;
        }

        App.Options = options;
        App.Instance = instance;
        try
        {
            // The app lives in the tray: closing its window is not leaving it.
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, ShutdownMode.OnExplicitShutdown);
        }
        finally
        {
            instance.Dispose();
        }
    }

    // Also what the XAML previewer calls.
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
