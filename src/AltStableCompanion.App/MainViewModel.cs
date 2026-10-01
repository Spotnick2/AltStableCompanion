using System.Diagnostics;
using System.Runtime.InteropServices;
using AltStableCompanion.Core;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace AltStableCompanion.App;

/// <summary>One line of the list, as text. What it says is decided in Core (PassText).</summary>
/// <param name="Summary">The line under the name.</param>
/// <param name="Tooltip">The full detail, when the line is not already it, or when there is
///   something to add to it; null means no tooltip.</param>
/// <param name="Thumbnail">The portrait, small; null for a row without one, or one that could
///   not be read this time. The same instance for the same file, so two lines compare equal.</param>
internal sealed record PortraitLine(string Name, string State, string Summary, string? Tooltip, bool Ready, bool Attention,
    IImage? Thumbnail)
{
    public bool Quiet => !Attention;
    public bool HasSummary => Summary.Length > 0;

    public static PortraitLine From(PortraitRow row, DateTime today, IImage? thumbnail, bool previewFailed)
    {
        var detail = PassText.RowDetail(row, today);
        var summary = PassText.RowSummary(row, today);
        return new(row.Name, PassText.RowState(row), summary ?? detail, PassText.RowTooltip(summary, detail, previewFailed),
            row.Ready, row.NeedsAttention, thumbnail);
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
internal sealed class MainViewModel : ObservableObject, IDisposable
{
    /// <summary>Twice the 48 px a thumbnail is drawn at: crisp at 200% scaling.</summary>
    private const int ThumbnailHeight = 96;

    private readonly Controller _controller;
    private readonly ThumbnailCache<Bitmap> _thumbnails = new(ThumbnailHeight, ToBitmap, b => b.Dispose());
    private bool _wantThumbnails;
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
    private bool _enhance;
    private string _enhanceMinLevel = "10";
    private string _enhanceStyle = EnhanceStyles.WowLike;
    private string? _codexStatus;
    private bool _rosterCapable;
    private bool _codexProbed;
    private bool _showHelp;
    private string _search = "";
    private IReadOnlyList<PortraitRow> _rows = [];
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
        RemakeEnhanced = new Command(controller.RemakeEnhanced);
        Resume = new Command(() => controller.SetPaused(false));
        StartWatching = new Command(controller.StartWatching);
        ToggleSettings = new Command(() => ShowSettings = !ShowSettings);
        ToggleHelp = new Command(() => ShowHelp = !ShowHelp);
        ClearSearch = new Command(() => Search = "");
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
    public Command ClearSearch { get; }

    /// <summary>
    /// Set once the window has been shown: until then the pictures are read for nobody. The
    /// Refresh that shows the window reads them.
    /// </summary>
    public bool WantThumbnails { get => _wantThumbnails; set => _wantThumbnails = value; }

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

    /// <summary>What an empty list says: nothing yet, or nothing by that name.</summary>
    public string EmptyText => Searching ? "No character by that name." : "Nothing here yet.";

    // ---- settings
    // Settings and Help are pages: one at a time, and the dashboard while neither.
    public bool ShowSettings
    {
        get => _showSettings;
        set
        {
            if (!Set(ref _showSettings, value)) return;
            if (value) ShowHelp = false;
            Raise(nameof(ShowDashboard));
        }
    }

    public bool ShowDashboard => !_showSettings && !_showHelp;
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

    // ---- enhanced portraits: the one thing that leaves the PC, off unless chosen
    public bool Enhance
    {
        get => _enhance;
        set { if (Set(ref _enhance, value)) Apply(); }
    }

    /// <summary>
    /// The box's text, handed over when the player leaves the box or presses Enter - never per
    /// keystroke: a "2" on the way to "25" would spend generations on level-2 alts. Digits
    /// that make a level apply; anything else goes back to the level in force.
    /// </summary>
    public string EnhanceMinLevel
    {
        get => _enhanceMinLevel;
        set
        {
            if (int.TryParse(value.Trim(), out var level) && level >= 1)
            {
                Set(ref _enhanceMinLevel, value);
                Apply();
            }
            else
            {
                // Raised whether or not the field changes: the box shows the nonsense, the field
                // never held it, and the box must be told again.
                _enhanceMinLevel = _knownLevel.ToString(System.Globalization.CultureInfo.InvariantCulture);
                Raise(nameof(EnhanceMinLevel));
            }
            Raise(nameof(EnhanceExplanation));
        }
    }

    private int _knownLevel = Settings.DefaultEnhanceMinLevel;

    /// <summary>The box can be used when it can be turned on - or is on: what is on must be turn-off-able.</summary>
    public bool EnhanceUsable => CanEnhance || _enhance;

    /// <summary>Pictures a settings change held back, and the offer to make them again.</summary>
    public string EnhanceHeld => PassText.EnhanceHeld(_controller.Current.Shell.EnhanceHeld) ?? "";
    public bool HasEnhanceHeld => EnhanceHeld.Length > 0;
    public string EnhanceHeldButton => PassText.EnhanceHeldButton(_controller.Current.Shell.EnhanceHeld);
    public Command RemakeEnhanced { get; }

    public bool IsWowLike { get => _enhanceStyle == EnhanceStyles.WowLike; set { if (value) SetStyle(EnhanceStyles.WowLike); } }
    public bool IsRealistic { get => _enhanceStyle == EnhanceStyles.Realistic; set { if (value) SetStyle(EnhanceStyles.Realistic); } }
    public bool IsCartoonish { get => _enhanceStyle == EnhanceStyles.Cartoonish; set { if (value) SetStyle(EnhanceStyles.Cartoonish); } }

    private void SetStyle(string style)
    {
        if (_enhanceStyle == style) return;
        _enhanceStyle = style;
        Apply();
    }

    private void Apply()
    {
        var level = int.TryParse(_enhanceMinLevel.Trim(), out var l) && l >= 1 ? l : Settings.DefaultEnhanceMinLevel;
        _controller.SetEnhance(_enhance, level, _enhanceStyle);
    }

    public string EnhanceExplanation => PassText.EnhanceExplanation(
        int.TryParse(_enhanceMinLevel.Trim(), out var l) && l >= 1 ? l : Settings.DefaultEnhanceMinLevel, _codexStatus);

    /// <summary>Why the box cannot be used, or null. Until the CLI has been looked for, nothing is said against it.</summary>
    public string? EnhanceUnavailable => PassText.EnhanceUnavailable(codexFound: !_codexProbed || _codexStatus is not null, rosterCapable: _rosterCapable);
    public bool CanEnhance => EnhanceUnavailable is null;
    public bool HasEnhanceUnavailable => EnhanceUnavailable is not null;

    public bool IsClear { get => _skin == Skins.Clear; set { if (value) _controller.SetSkin(Skins.Clear); } }
    public bool IsSmoked { get => _skin == Skins.Smoked; set { if (value) _controller.SetSkin(Skins.Smoked); } }
    public bool IsFlat { get => _skin == Skins.Flat; set { if (value) _controller.SetSkin(Skins.Flat); } }

    // ---- help
    public bool ShowHelp
    {
        get => _showHelp;
        set
        {
            if (!Set(ref _showHelp, value)) return;
            if (value) ShowSettings = false;
            Raise(nameof(ShowDashboard));
        }
    }

    // ---- the search box above the list
    public string Search
    {
        get => _search;
        set
        {
            if (!Set(ref _search, value)) return;
            Raise(nameof(Searching));
            Raise(nameof(EmptyText));
            ShowRows(DateTime.Now);
        }
    }

    public bool Searching => _search.Trim().Length > 0;

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
        // Enhancement, from the controller: through the fields, then the derived words.
        Set(ref _enhance, now.Enhance, nameof(Enhance));
        // The box's text follows the level when the level changed - not on every refresh, which
        // would overwrite what is being typed.
        if (_knownLevel != now.EnhanceMinLevel)
        {
            _knownLevel = now.EnhanceMinLevel;
            Set(ref _enhanceMinLevel, now.EnhanceMinLevel.ToString(System.Globalization.CultureInfo.InvariantCulture), nameof(EnhanceMinLevel));
        }
        if (_enhanceStyle != now.EnhanceStyle)
        {
            _enhanceStyle = now.EnhanceStyle;
            Raise(nameof(IsWowLike)); Raise(nameof(IsRealistic)); Raise(nameof(IsCartoonish));
        }
        _codexStatus = now.CodexStatus;
        _codexProbed = now.CodexProbed;
        _rosterCapable = shell.Install?.RosterDrawsEnhanced ?? false;
        Raise(nameof(EnhanceExplanation)); Raise(nameof(EnhanceUnavailable)); Raise(nameof(CanEnhance)); Raise(nameof(HasEnhanceUnavailable)); Raise(nameof(EnhanceUsable));
        Raise(nameof(EnhanceHeld)); Raise(nameof(HasEnhanceHeld)); Raise(nameof(EnhanceHeldButton));

        _rows = shell.Report?.Portraits ?? [];
        Count = PassText.Count(_rows);
        Attention = PassText.Attention(_rows);
        ShowRows(today);

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

    // The rows the list shows: the report's, narrowed by the search box. The pictures are
    // read here, on the UI thread: a few hundred KB each, once per file.
    private void ShowRows(DateTime today)
    {
        var install = _controller.Current.Shell.Install;
        var cutouts = install is null || !_wantThumbnails ? null : new CutoutFolder(install.CutoutAddonDir).CutoutsDir;
        var lines = PassText.Matching(_rows, _search).Select(r =>
        {
            var failed = false;
            var thumbnail = cutouts is null ? null : _thumbnails.Get(cutouts, r, out failed);
            return PortraitLine.From(r, today, thumbnail, failed);
        }).ToList();
        if (!lines.SequenceEqual(_portraits)) Portraits = lines;
        // Only now, with the rows that showed them replaced.
        _thumbnails.Sweep();
    }

    public void Dispose() => _thumbnails.Dispose();

    // What the window draws: the same bytes, straight (unpremultiplied) RGBA, copied into an
    // immutable bitmap at 96 dpi, so a pixel is a device-independent pixel and the Image's
    // slot does the scaling. (A WriteableBitmap filled through Lock() drew some sizes and not
    // others on this machine; this constructor draws them all.)
    private static Bitmap ToBitmap(RgbaImage img)
    {
        var pinned = GCHandle.Alloc(img.Pixels, GCHandleType.Pinned);
        try
        {
            return new Bitmap(PixelFormat.Rgba8888, AlphaFormat.Unpremul, pinned.AddrOfPinnedObject(),
                new PixelSize(img.Width, img.Height), new Vector(96, 96), img.Width * 4);
        }
        finally
        {
            pinned.Free();
        }
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
