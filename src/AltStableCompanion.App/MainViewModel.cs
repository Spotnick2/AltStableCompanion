using System.Diagnostics;
using System.Globalization;
using AltStableCompanion.Core;

namespace AltStableCompanion.App;

/// <summary>One character in the list.</summary>
internal sealed record CharacterRow(string Name, string State, string Captured, string? Note)
{
    public bool HasNote => !string.IsNullOrEmpty(Note);

    public static CharacterRow From(CharacterStatus s) => new(
        s.Name,
        PassText.StateLabel(s.State),
        s.LastCaptured.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
        s.Note);
}

/// <summary>
/// The window's state, read from the controller's snapshot. Always on the UI thread: the
/// snapshot is read when the update RUNS, not when it was asked for, so a report from an
/// install the player has since left is never shown.
/// </summary>
internal sealed class MainViewModel : ObservableObject
{
    private readonly Controller _controller;
    private string _installPath = "";
    private string _installDetail = "";
    private string? _flavorWarning;
    private string? _browseProblem;
    private string _statusLine = "";
    private string? _activity;
    private bool _canChangeInstall = true;
    private bool _keepScreenshots;
    private bool _restartNotice;
    private IReadOnlyList<CharacterRow> _characters = [];
    private IReadOnlyList<string> _warnings = [];

    public MainViewModel(Controller controller)
    {
        _controller = controller;
        ConvertNow = new Command(controller.ConvertNow);
        OpenCutouts = new Command(() => Open(CutoutsFolder()));
        OpenLog = new Command(() => Open(File.Exists(controller.LogPath) ? controller.LogPath : controller.DataDir));
        DetectAgain = new Command(() =>
        {
            BrowseProblem = null;
            controller.DetectAgain();
        });
        DismissRestartNotice = new Command(controller.DismissRestartNotice);
        Refresh();
    }

    public Command ConvertNow { get; }
    public Command OpenCutouts { get; }
    public Command OpenLog { get; }
    public Command DetectAgain { get; }
    public Command DismissRestartNotice { get; }

    public string RestartNoticeText => PassText.RestartNotice;

    public string InstallPath { get => _installPath; private set => Set(ref _installPath, value); }
    public string InstallDetail { get => _installDetail; private set => Set(ref _installDetail, value); }
    public string StatusLine { get => _statusLine; private set => Set(ref _statusLine, value); }

    public string? Activity
    {
        get => _activity;
        private set { if (Set(ref _activity, value)) Raise(nameof(HasActivity)); }
    }

    public bool HasActivity => _activity is not null;
    public bool CanChangeInstall { get => _canChangeInstall; private set => Set(ref _canChangeInstall, value); }
    public bool RestartNotice { get => _restartNotice; private set => Set(ref _restartNotice, value); }

    public string? FlavorWarning
    {
        get => _flavorWarning;
        private set { if (Set(ref _flavorWarning, value)) Raise(nameof(HasFlavorWarning)); }
    }

    public bool HasFlavorWarning => _flavorWarning is not null;

    public string? BrowseProblem
    {
        get => _browseProblem;
        private set { if (Set(ref _browseProblem, value)) Raise(nameof(HasBrowseProblem)); }
    }

    public bool HasBrowseProblem => _browseProblem is not null;

    public IReadOnlyList<CharacterRow> Characters
    {
        get => _characters;
        private set { if (Set(ref _characters, value)) Raise(nameof(HasCharacters)); }
    }

    public bool HasCharacters => _characters.Count > 0;

    public IReadOnlyList<string> Warnings
    {
        get => _warnings;
        private set { if (Set(ref _warnings, value)) Raise(nameof(HasWarnings)); }
    }

    public bool HasWarnings => _warnings.Count > 0;

    public bool KeepScreenshots
    {
        get => _keepScreenshots;
        set { if (Set(ref _keepScreenshots, value)) _controller.SetKeepScreenshots(value); }
    }

    /// <summary>The folder the player picked with Browse.</summary>
    public void Picked(string folder)
    {
        BrowseProblem = _controller.Browse(folder)
            ? null
            : $"No Wow*.exe in {folder} - pick the World of Warcraft folder, or {WowInstallLocator.DefaultFlavor} inside it.";
    }

    /// <summary>Bring everything in line with the controller. UI thread.</summary>
    public void Refresh()
    {
        var now = _controller.Current;
        var shell = now.Shell;

        InstallPath = shell.Install?.FlavorDir ?? "";
        InstallDetail = now.Pinned && shell.Install is not null
            ? PassText.InstallDetail(shell.Install, shell.Report) + " - pinned by --wow-dir"
            : shell.Install is null ? "" : PassText.InstallDetail(shell.Install, shell.Report);
        FlavorWarning = shell.Install is null ? null : PassText.FlavorWarning(shell.Install);
        CanChangeInstall = !now.Pinned && !shell.Stopping;
        StatusLine = PassText.StatusLine(shell);
        Activity = PassText.Activity(shell, DateTime.Now);
        RestartNotice = now.RestartNotice;

        // Through the field: this is the controller telling the window, not the player.
        Set(ref _keepScreenshots, now.KeepScreenshots, nameof(KeepScreenshots));

        var rows = (shell.Report?.Characters ?? []).Select(CharacterRow.From).ToList();
        if (!rows.SequenceEqual(_characters)) Characters = rows;

        var warnings = new List<string>(shell.Report?.Warnings ?? []);
        if (now.SettingsProblem is not null) warnings.Add(now.SettingsProblem);
        if (shell.LastError is not null) warnings.Add(shell.LastError);
        if (!warnings.SequenceEqual(_warnings)) Warnings = warnings;

        var usable = shell.Install is not null && !shell.Stopping;
        ConvertNow.Enabled = usable;
        OpenCutouts.Enabled = usable;
        DetectAgain.Enabled = CanChangeInstall;
    }

    // The cutouts folder, or the nearest folder above it that exists: before the first
    // portrait there is no AltStableCutouts, and there may be no AddOns either.
    private string? CutoutsFolder()
    {
        var install = _controller.Current.Shell.Install;
        if (install is null) return null;
        var dir = install.CutoutAddonDir;
        while (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) dir = Path.GetDirectoryName(dir);
        return dir;
    }

    private static void Open(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            // Nothing to open it with, or it went away. Not worth a dialog.
        }
    }
}
