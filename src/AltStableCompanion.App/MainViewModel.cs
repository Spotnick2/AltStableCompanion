using System.Diagnostics;
using AltStableCompanion.Core;

namespace AltStableCompanion.App;

/// <summary>One line of the list, as text. What it says is decided in Core (PassText).</summary>
/// <param name="Summary">The line under the name.</param>
/// <param name="Tooltip">The full detail, when the line is not already it; null means no tooltip.</param>
internal sealed record PortraitLine(string Name, string State, string Summary, string? Tooltip, bool Ready, bool Attention)
{
    public bool Quiet => !Attention;
    public bool HasSummary => Summary.Length > 0;

    public static PortraitLine From(PortraitRow row, DateTime today)
    {
        var detail = PassText.RowDetail(row, today);
        var summary = PassText.RowSummary(row, today);
        return new(row.Name, PassText.RowState(row), summary ?? detail, summary is null ? null : detail,
            row.Ready, row.NeedsAttention);
    }
}

/// <summary>
/// The window's state, read from the controller's snapshot. Always on the UI thread: the
/// snapshot is read when the update RUNS, not when it was asked for, so a report from an
/// install the player has since left is never shown.
///
/// Nothing is decided here. Which headline, which next step, what a row says: that is Core's,
/// where it is tested. This turns it into properties a window can bind to.
/// </summary>
internal sealed class MainViewModel : ObservableObject
{
    private readonly Controller _controller;
    private string _game = "";
    private string _addon = "";
    private string? _accounts;
    private bool _addonDetected;
    private string _installPath = "";
    private string? _installNote;
    private string? _flavorWarning;
    private string? _browseProblem;
    private Headline _headline = new("", null, HeadlineKind.Busy);
    private string? _activity;
    private string _count = "";
    private string? _attention;
    private string? _problems;
    private bool _canChangeInstall = true;
    private bool _keepScreenshots;
    private bool _paused;
    private bool _restartNotice;
    private bool _firstStart;
    private bool _showSettings;
    private string _skin = Skins.Clear;
    private bool _showHelp;
    private IReadOnlyList<PortraitLine> _portraits = [];
    private IReadOnlyList<string> _warnings = [];

    public MainViewModel(Controller controller)
    {
        _controller = controller;
        Check = new Command(controller.ConvertNow);
        OpenCutouts = new Command(() => Open(CutoutsFolder()));
        OpenLog = new Command(() => Open(File.Exists(controller.LogPath) ? controller.LogPath : controller.DataDir));
        DetectAgain = new Command(() =>
        {
            BrowseProblem = null;
            controller.DetectAgain();
        });
        DismissRestartNotice = new Command(controller.DismissRestartNotice);
        GotIt = new Command(controller.AcknowledgeUpdate);
        Resume = new Command(() => controller.SetPaused(false));
        StartWatching = new Command(controller.StartWatching);
        ToggleSettings = new Command(() => ShowSettings = !ShowSettings);
        ToggleHelp = new Command(() => ShowHelp = !ShowHelp);
        Refresh();
    }

    public Command Check { get; }
    public Command OpenCutouts { get; }
    public Command OpenLog { get; }
    public Command DetectAgain { get; }
    public Command DismissRestartNotice { get; }
    public Command GotIt { get; }
    public Command Resume { get; }
    public Command StartWatching { get; }
    public Command ToggleSettings { get; }
    public Command ToggleHelp { get; }

    public string RestartNoticeText => PassText.RestartNotice;
    public string FirstStartText => PassText.FirstStart;
    public string AddHint => PassText.AddHint;
    public IReadOnlyList<string> HowItWorks => PassText.HowItWorks;

    // ---- the top line
    public string Game { get => _game; private set => Set(ref _game, value); }
    public string Addon { get => _addon; private set => Set(ref _addon, value); }
    // An empty line still takes a line: before the first pass there is no account count, and
    // the addon line would sit above the middle.
    public string? Accounts
    {
        get => _accounts;
        private set { if (Set(ref _accounts, value)) Raise(nameof(HasAccounts)); }
    }

    public bool HasAccounts => _accounts is not null;

    public bool AddonDetected
    {
        get => _addonDetected;
        private set { if (Set(ref _addonDetected, value)) Raise(nameof(AddonMissing)); }
    }

    public bool AddonMissing => !_addonDetected;

    public string? FlavorWarning
    {
        get => _flavorWarning;
        private set { if (Set(ref _flavorWarning, value)) Raise(nameof(HasFlavorWarning)); }
    }

    public bool HasFlavorWarning => _flavorWarning is not null;

    // ---- the headline
    public string Title => _headline.Title;
    public string? Next => _headline.Next;
    public bool HasNext => _headline.Next is not null;
    public bool Dismissable => _headline.Dismissable;
    public bool IsGood => _headline.Kind == HeadlineKind.Good;
    public bool IsBusy => _headline.Kind == HeadlineKind.Busy;
    public bool IsInfo => _headline.Kind == HeadlineKind.Info;
    public bool IsAttention => _headline.Kind == HeadlineKind.Attention;
    public bool IsProblem => _headline.Kind == HeadlineKind.Problem;

    public string? Activity
    {
        get => _activity;
        private set { if (Set(ref _activity, value)) Raise(nameof(HasActivity)); }
    }

    public bool HasActivity => _activity is not null;

    public string? Problems
    {
        get => _problems;
        private set { if (Set(ref _problems, value)) Raise(nameof(HasProblems)); }
    }

    public bool HasProblems => _problems is not null;

    public bool RestartNotice { get => _restartNotice; private set => Set(ref _restartNotice, value); }

    // ---- the first start
    public bool FirstStart
    {
        get => _firstStart;
        private set { if (Set(ref _firstStart, value)) Raise(nameof(Started)); }
    }

    public bool Started => !_firstStart;

    // ---- the list
    public string Count { get => _count; private set => Set(ref _count, value); }

    public string? Attention
    {
        get => _attention;
        private set { if (Set(ref _attention, value)) Raise(nameof(HasAttention)); }
    }

    public bool HasAttention => _attention is not null;

    public IReadOnlyList<PortraitLine> Portraits
    {
        get => _portraits;
        private set { if (Set(ref _portraits, value)) Raise(nameof(HasPortraits)); }
    }

    public bool HasPortraits => _portraits.Count > 0;

    // ---- settings
    public bool ShowSettings { get => _showSettings; set => Set(ref _showSettings, value); }
    public string InstallPath { get => _installPath; private set => Set(ref _installPath, value); }
    public bool CanChangeInstall { get => _canChangeInstall; private set => Set(ref _canChangeInstall, value); }

    public string? InstallNote
    {
        get => _installNote;
        private set { if (Set(ref _installNote, value)) Raise(nameof(HasInstallNote)); }
    }

    public bool HasInstallNote => _installNote is not null;

    public string? BrowseProblem
    {
        get => _browseProblem;
        private set { if (Set(ref _browseProblem, value)) Raise(nameof(HasBrowseProblem)); }
    }

    public bool HasBrowseProblem => _browseProblem is not null;

    public bool KeepScreenshots
    {
        get => _keepScreenshots;
        set { if (Set(ref _keepScreenshots, value)) _controller.SetKeepScreenshots(value); }
    }

    public bool Paused
    {
        get => _paused;
        set { if (Set(ref _paused, value)) { Raise(nameof(Automatic)); _controller.SetPaused(value); } }
    }

    /// <summary>The setting as the player reads it: on means new captures are processed.</summary>
    public bool Automatic { get => !_paused; set => Paused = !value; }

    /// <summary>One of <see cref="Skins"/>. The window listens for it.</summary>
    public string Skin
    {
        get => _skin;
        private set { if (Set(ref _skin, value)) { Raise(nameof(IsClear)); Raise(nameof(IsSmoked)); Raise(nameof(IsFlat)); } }
    }

    public bool IsClear { get => _skin == Skins.Clear; set { if (value) _controller.SetSkin(Skins.Clear); } }
    public bool IsSmoked { get => _skin == Skins.Smoked; set { if (value) _controller.SetSkin(Skins.Smoked); } }
    public bool IsFlat { get => _skin == Skins.Flat; set { if (value) _controller.SetSkin(Skins.Flat); } }

    // ---- help
    public bool ShowHelp { get => _showHelp; set => Set(ref _showHelp, value); }

    public IReadOnlyList<string> Warnings
    {
        get => _warnings;
        private set { if (Set(ref _warnings, value)) Raise(nameof(HasWarnings)); }
    }

    public bool HasWarnings => _warnings.Count > 0;

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
        var today = DateTime.Now;

        if (shell.Install is { } install)
        {
            var (game, addon, accounts) = PassText.InstallSummary(install, shell.Report);
            (Game, Addon, Accounts) = (game, addon, accounts);
            AddonDetected = install.AltStableInstalled;
            FlavorWarning = PassText.FlavorWarning(install);
        }
        else
        {
            (Game, Addon, Accounts) = ("World of Warcraft", "folder not set", null);
            AddonDetected = false;
            FlavorWarning = null;
        }

        InstallPath = shell.Install?.FlavorDir ?? "";
        InstallNote = now.Pinned ? "Set by --wow-dir for this run." : null;
        CanChangeInstall = !now.Pinned && !shell.Stopping;

        var headline = PassText.Headline(shell, now.RestartNotice);
        if (headline != _headline)
        {
            _headline = headline;
            foreach (var name in new[]
                     {
                         nameof(Title), nameof(Next), nameof(HasNext), nameof(Dismissable), nameof(IsGood),
                         nameof(IsBusy), nameof(IsInfo), nameof(IsAttention), nameof(IsProblem),
                     })
            {
                Raise(name);
            }
        }
        Activity = PassText.Activity(shell, today);
        RestartNotice = now.RestartNotice;
        FirstStart = shell.FirstStart && shell.Install is not null;

        // Through the fields: this is the controller telling the window, not the player.
        Set(ref _keepScreenshots, now.KeepScreenshots, nameof(KeepScreenshots));
        if (Set(ref _paused, shell.Paused, nameof(Paused))) Raise(nameof(Automatic));
        Skin = now.Skin;

        var rows = shell.Report?.Portraits ?? [];
        Count = PassText.Count(rows);
        Attention = PassText.Attention(rows);
        var lines = rows.Select(r => PortraitLine.From(r, today)).ToList();
        if (!lines.SequenceEqual(_portraits)) Portraits = lines;

        var warnings = new List<string>(shell.Report?.Warnings ?? []);
        if (now.SettingsProblem is not null) warnings.Insert(0, now.SettingsProblem);
        if (shell.LastError is not null) warnings.Insert(0, "The last check failed: " + shell.LastError);
        if (!warnings.SequenceEqual(_warnings)) Warnings = warnings;
        Problems = warnings.Count switch
        {
            0 => null,
            1 => "1 warning",
            var n => $"{n} warnings",
        };

        var usable = shell.Install is not null && !shell.Stopping;
        Check.Enabled = usable && !shell.FirstStart;
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
