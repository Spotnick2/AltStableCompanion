using System.Diagnostics;
using System.Runtime.InteropServices;
using AltStableCompanion.Core;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;

namespace AltStableCompanion.App;

/// <summary>One line of the list, as text. What it says is decided in Core (PassText).</summary>
/// <param name="Summary">The line under the name.</param>
/// <param name="Tooltip">The full detail, when the line is not already it, or when there is
///   something to add to it; null means no tooltip.</param>
/// <param name="Thumbnail">The portrait, small; null for a row without one, or one that could
///   not be read this time. The same instance for the same file, so two lines compare equal.</param>
/// <param name="CheckNow">The armory's hold keeps it from this character: the row offers to ask it now.</param>
internal sealed record PortraitLine(string Name, string State, string Summary, string? Tooltip, bool Ready, bool Attention,
    IImage? Thumbnail, bool Working = false, string? Guid = null, bool CheckNow = false)
{
    public bool Quiet => !Attention;
    public bool HasSummary => Summary.Length > 0;
    /// <summary>The green dot: in the manifest, and not being worked on right now.</summary>
    public bool Idle => Ready && !Working;

    public static PortraitLine From(PortraitRow row, DateTime today, IImage? thumbnail, bool previewFailed)
    {
        var detail = PassText.RowDetail(row, today);
        var summary = PassText.RowSummary(row, today);
        return new(row.Name, PassText.RowState(row), summary ?? detail, PassText.RowTooltip(summary, detail, previewFailed),
            row.Ready, row.NeedsAttention, thumbnail, row.Enhancing, row.Guid, row.ArmoryHeldUntil is not null && row.Guid is not null);
    }
}

/// <summary>A link under About: the browser opens it.</summary>
internal sealed record AboutLink(string Label, string Url, string Tip)
{
    public Command Open { get; } = new(() => MainViewModel.OpenUrl(Url));
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
    private string? _altStableElsewhere;
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
    private bool _confirmDiagnostics;
    private bool _confirmEnhance;
    private string _enhanceConfirmText = "";
    private string _enhanceConfirmButton = "Turn on";
    private int _planAsked;
    private EnhancePlan? _plan;          // the count the question shows, once it is in
    private bool _planChanged;           // the count changed under a press of Start: say so
    private bool _turningOn;             // Start pressed, Core not answered yet: the buttons stay off
    private int _press;                  // that press's ticket: what the player's backing out withdraws
    private string? _diagnosticsLine;
    private bool _diagnosticsFailed;
    private bool _checkUpdatesOnOpen;
    private bool? _armory;
    private bool _armorySd;
    private int _armoryHours = Core.Armory.DefaultRemakeHours;
    private bool _armoryInDays = true;
    private string? _armorySweepText;
    private bool _hideGone;
    private string? _goneLine;
    private IReadOnlyList<string> _cleanUpLines = [];
    private IReadOnlyList<string> _cleanUpGuids = [];
    private bool _cleanUpAsking;
    private bool _cleaningUp;
    private string? _cleanUpResult;
    private bool _armoryAsk;
    private bool _armoryAuto = true;
    private bool _armoryLimit = true;
    private int _armoryMax = Core.Armory.DefaultDailyMax;
    private BlizzardClientState _blizzard;
    private string _blizzardId = "";
    private string _blizzardSecret = "";
    private string? _blizzardNote;
    private UpdateState _update = new();
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
        StartEnhance = new Command(TurnOn);
        CancelEnhance = new Command(BackOut);
        Resume = new Command(() => controller.SetPaused(false));
        StartWatching = new Command(controller.StartWatching);
        AnswerArmoryYes = new Command(() => controller.SetArmory(true));
        AnswerArmoryNo = new Command(() => controller.SetArmory(false));
        CheckArmory = new Command(controller.CheckArmoryNow);
        CheckArmoryFor = new Command<string>(controller.CheckArmoryNow);
        AskCleanUp = new Command(AskToCleanUp);
        CancelCleanUp = new Command(() => CleanUpAsking = false);
        ConfirmCleanUp = new Command(CleanUp);
        SaveBlizzard = new Command(SaveBlizzardClient);
        TestBlizzard = new Command(TestBlizzardClient);
        RemoveBlizzard = new Command(() => BlizzardNote = controller.RemoveBlizzardClient());
        OpenBlizzardClients = new Command(() => OpenUrl(PassText.BlizzardClientsUrl));
        ToggleSettings = new Command(() => ShowSettings = !ShowSettings);
        ToggleHelp = new Command(() => ShowHelp = !ShowHelp);
        ClearSearch = new Command(() => Search = "");
        CheckUpdates = new Command(controller.CheckForUpdates);
        DownloadUpdate = new Command(controller.DownloadUpdate);
        RestartToUpdate = new Command(() => RestartRequested?.Invoke());
        OpenReleasePage = new Command(() => OpenUrl(_update.Release?.Page));
        AskDiagnostics = new Command(() => ConfirmDiagnostics = true);
        CancelDiagnostics = new Command(() => ConfirmDiagnostics = false);
        SaveDiagnostics = new Command(SaveDiagnosticsFile);
        AboutLinks = [.. PassText.AboutLinks.Select(l => new AboutLink(l.Label, l.Url, l.Tip))];
        VersionLine = PassText.VersionLine(controller.Version);
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

    // ---- portraits from the armory (#63): asked once, never defaulted
    public Command AnswerArmoryYes { get; }
    public Command AnswerArmoryNo { get; }
    public Command CheckArmory { get; }
    /// <summary>A held row's "Check now": the armory, for that character, past the hold.</summary>
    public Command<string> CheckArmoryFor { get; }
    public string CheckNowText => PassText.CheckNow;
    public string ArmoryQuestion => PassText.ArmoryQuestion;
    public string ArmoryYesText => PassText.ArmoryYes;
    public string ArmoryNoText => PassText.ArmoryNo;

    /// <summary>Not answered yet: the question is on the dashboard until it is.</summary>
    public bool ArmoryAsk { get => _armoryAsk; private set => Set(ref _armoryAsk, value); }

    public bool Armory
    {
        get => _armory == true;
        set { if (Set(ref _armory, value)) { Raise(nameof(ArmoryOn)); _controller.SetArmory(value); } }
    }

    public bool ArmoryOn => _armory == true;

    // ---- the optional Battle.net API client (#63, M3): typed once, never shown again
    public Command SaveBlizzard { get; }
    public Command TestBlizzard { get; }
    public Command RemoveBlizzard { get; }
    public Command OpenBlizzardClients { get; }
    public string BlizzardExplanation => PassText.BlizzardExplanation;
    public string BlizzardClientId { get => _blizzardId; set => Set(ref _blizzardId, value); }
    public string BlizzardClientSecret { get => _blizzardSecret; set => Set(ref _blizzardSecret, value); }

    /// <summary>A client is saved (refused or not): Test and Remove, no boxes.</summary>
    public bool BlizzardSaved => _blizzard is BlizzardClientState.Saved or BlizzardClientState.Refused;
    /// <summary>Remove shows for a saved client, and for one whose file would not be deleted, to try again.</summary>
    public bool BlizzardRemovable => BlizzardSaved || _blizzard is BlizzardClientState.NotDeleted;
    public bool BlizzardEntering => !BlizzardSaved;

    /// <summary>The last Test's or Save's words, else where the client stands.</summary>
    public string BlizzardStatus => _blizzardNote ?? PassText.BlizzardState(_blizzard);

    private string? BlizzardNote
    {
        get => _blizzardNote;
        set { if (Set(ref _blizzardNote, value)) Raise(nameof(BlizzardStatus)); }
    }

    private void SaveBlizzardClient()
    {
        var problem = _controller.SetBlizzardClient(_blizzardId, _blizzardSecret);
        if (problem is null)
        {
            // Out of the window's memory too: what was typed is never shown again.
            BlizzardClientId = "";
            BlizzardClientSecret = "";
        }
        BlizzardNote = problem;
    }

    private void TestBlizzardClient()
    {
        TestBlizzard.Enabled = false;
        BlizzardNote = "Asking Battle.net...";
        _controller.TestBlizzardClientAsync().ContinueWith(t => Dispatcher.UIThread.Post(() =>
        {
            TestBlizzard.Enabled = true;
            BlizzardNote = t.IsCompletedSuccessfully ? t.Result : "Battle.net could not be asked.";
        }));
    }

    public bool ArmoryHd
    {
        get => !_armorySd;
        set { if (value && Set(ref _armorySd, false, nameof(ArmorySd))) { Raise(nameof(ArmoryHd)); _controller.SetArmorySd(false); } }
    }

    public bool ArmorySd
    {
        get => _armorySd;
        set { if (value && Set(ref _armorySd, true)) { Raise(nameof(ArmoryHd)); _controller.SetArmorySd(true); } }
    }

    /// <summary>The spinner's value, in the unit beside it: 1 to 90 days, or 1 to 2160 hours; anything else is put back.</summary>
    public decimal? ArmoryRemakeAmount
    {
        get => _armoryInDays ? _armoryHours / 24 : _armoryHours;
        set
        {
            var amount = (int)Math.Clamp(Math.Round(value ?? (decimal)ArmoryRemakeAmount!), 1, ArmoryRemakeMax);
            SetRemake(amount, _armoryInDays);
            if (value != amount) Raise(nameof(ArmoryRemakeAmount));
        }
    }

    /// <summary>The unit list's choice: 0 hours, 1 days. The number stays; what it counts changes.</summary>
    public int ArmoryRemakeUnit
    {
        get => _armoryInDays ? 1 : 0;
        set
        {
            if (value is not (0 or 1) || (value == 1) == _armoryInDays) return;
            var amount = (int)ArmoryRemakeAmount!;
            SetRemake(Math.Min(amount, value == 1 ? Core.Armory.MaxRemakeHours / 24 : Core.Armory.MaxRemakeHours), value == 1);
        }
    }

    public int ArmoryRemakeMax => _armoryInDays ? Core.Armory.MaxRemakeHours / 24 : Core.Armory.MaxRemakeHours;

    private void SetRemake(int amount, bool inDays)
    {
        var hours = inDays ? amount * 24 : amount;
        if (Set(ref _armoryHours, hours, nameof(ArmoryRemakeAmount)) | Set(ref _armoryInDays, inDays, nameof(ArmoryRemakeUnit)))
        {
            Raise(nameof(ArmoryRemakeAmount));
            Raise(nameof(ArmoryRemakeMax));
            Raise(nameof(ArmoryExplanation));
            _controller.SetArmoryRemake(amount, inDays);
        }
    }

    public string ArmoryExplanation => PassText.ArmoryExplanation(_armoryHours, _armoryInDays, _armoryAuto, _armoryLimit, _armoryMax);

    /// <summary>The line under "Check the armory now": its progress, then what it found; null before the first press.</summary>
    public string? ArmorySweepText
    {
        get => _armorySweepText;
        private set => Set(ref _armorySweepText, value);
    }

    /// <summary>With enhancement on: a new armory picture is enhanced on its own, or offered.</summary>
    public bool ArmoryEnhanceAuto
    {
        get => _armoryAuto;
        set { if (Set(ref _armoryAuto, value)) { Raise(nameof(ArmoryExplanation)); _controller.SetArmoryEnhance(value, _armoryLimit, _armoryMax); } }
    }

    public bool ArmoryDailyLimit
    {
        get => _armoryLimit;
        set { if (Set(ref _armoryLimit, value)) { Raise(nameof(ArmoryExplanation)); _controller.SetArmoryEnhance(_armoryAuto, value, _armoryMax); } }
    }

    public decimal? ArmoryDailyMax
    {
        get => _armoryMax;
        set
        {
            var max = (int)Math.Clamp(Math.Round(value ?? _armoryMax), 1, Core.Armory.MaxDailyMax);
            if (Set(ref _armoryMax, max, nameof(ArmoryDailyMax))) { Raise(nameof(ArmoryExplanation)); _controller.SetArmoryEnhance(_armoryAuto, _armoryLimit, max); }
            else if (value != max) Raise(nameof(ArmoryDailyMax));
        }
    }
    public Command ToggleSettings { get; }
    public Command ToggleHelp { get; }
    public Command ClearSearch { get; }
    public Command CheckUpdates { get; }
    public Command DownloadUpdate { get; }
    public Command RestartToUpdate { get; }
    public Command OpenReleasePage { get; }
    public Command AskDiagnostics { get; }
    public Command CancelDiagnostics { get; }
    public Command SaveDiagnostics { get; }

    /// <summary>"Restart now" was pressed with an update ready: the app's to do, not the window's.</summary>
    public event Action? RestartRequested;

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

    /// <summary>A detected install, and AltStable in another game beside it: which, and how to switch.</summary>
    public string? AltStableElsewhere
    {
        get => _altStableElsewhere;
        private set { if (Set(ref _altStableElsewhere, value)) Raise(nameof(HasAltStableElsewhere)); }
    }

    public bool HasAltStableElsewhere => _altStableElsewhere is not null;

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

    // ---- characters AltStable no longer has (#77)
    public Command AskCleanUp { get; }
    public Command CancelCleanUp { get; }
    public Command ConfirmCleanUp { get; }

    /// <summary>"2 characters AltStable no longer has", with the button that offers to clean them up.</summary>
    public string? GoneLine
    {
        get => _goneLine;
        private set { if (Set(ref _goneLine, value)) Raise(nameof(HasGone)); }
    }

    public bool HasGone => _goneLine is not null && !_cleanUpAsking;

    /// <summary>The question is open: these characters, and "Delete their files" or "Cancel".</summary>
    public bool CleanUpAsking
    {
        get => _cleanUpAsking;
        private set { if (Set(ref _cleanUpAsking, value)) Raise(nameof(HasGone)); }
    }

    public IReadOnlyList<string> CleanUpLines
    {
        get => _cleanUpLines;
        private set => Set(ref _cleanUpLines, value);
    }

    /// <summary>What the last clean-up did, until the next question.</summary>
    public string? CleanUpResult
    {
        get => _cleanUpResult;
        private set { if (Set(ref _cleanUpResult, value)) Raise(nameof(HasCleanUpResult)); }
    }

    public bool HasCleanUpResult => _cleanUpResult is not null;

    /// <summary>Settings: leave them out of the list, deleting nothing.</summary>
    public bool HideGone
    {
        get => _hideGone;
        set
        {
            if (!Set(ref _hideGone, value)) return;
            _controller.SetHideGone(value);
            ShowRows(DateTime.Today);
        }
    }

    // The question names exactly who the yes is for: what the list showed when it was asked.
    private void AskToCleanUp()
    {
        CleanUpLines = PassText.CleanUpLines(_rows);
        _cleanUpGuids = [.. _rows.Where(r => r.Gone && r.Guid is not null).Select(r => r.Guid!)];
        CleanUpResult = null;
        CleanUpAsking = _cleanUpGuids.Count > 0;
    }

    private void CleanUp()
    {
        if (_cleaningUp || _cleanUpGuids.Count == 0) return;
        _cleaningUp = true;
        ConfirmCleanUp.Enabled = false;
        CancelCleanUp.Enabled = false;
        _controller.CleanUpCharactersAsync(_cleanUpGuids).ContinueWith(t => Dispatcher.UIThread.Post(() =>
        {
            _cleaningUp = false;
            ConfirmCleanUp.Enabled = true;
            CancelCleanUp.Enabled = true;
            CleanUpAsking = false;
            CleanUpResult = t.IsCompletedSuccessfully ? t.Result : "The clean-up failed; the log says why.";
        }));
    }

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
    /// <summary>
    /// Ticking it asks first (<see cref="ConfirmEnhance"/>): it is on only once the player has
    /// seen how many pictures it makes. Every time - turning it on again later asks again.
    /// Unticking it while asked is Cancel.
    /// </summary>
    public bool Enhance
    {
        get => _enhance || _confirmEnhance;
        set
        {
            if (value == Enhance) return;
            if (value) { ConfirmEnhance = true; return; }
            if (_confirmEnhance) { BackOut(); return; }
            _enhance = false;
            Raise(nameof(Enhance));
            Apply();
        }
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

    /// <summary>What the enhancer is doing, or last did, under the switch.</summary>
    public string EnhanceStatus => PassText.EnhanceStatus(_controller.Current.Shell, DateTime.Now) ?? "";
    public bool HasEnhanceStatus => EnhanceStatus.Length > 0;
    /// <summary>Pictures a settings change held back, and the offer to make them again.</summary>
    public string EnhanceHeld => PassText.EnhanceHeld(_controller.Current.Shell.EnhanceHeld, _controller.Current.Shell.ArmoryHeld) ?? "";
    public bool HasEnhanceHeld => EnhanceHeld.Length > 0;
    public string EnhanceHeldButton => PassText.EnhanceHeldButton(_controller.Current.Shell.EnhanceHeld, _controller.Current.Shell.ArmoryHeld);
    public Command RemakeEnhanced { get; }
    public Command StartEnhance { get; }
    public Command CancelEnhance { get; }

    // ---- the question before turning it on: how many pictures, counted the worker's way

    /// <summary>The box was ticked and the player has not answered yet. The box shows ticked meanwhile.</summary>
    public bool ConfirmEnhance
    {
        get => _confirmEnhance;
        private set
        {
            if (!Set(ref _confirmEnhance, value)) return;
            Raise(nameof(Enhance));
            if (value) CountPictures();
            else _planAsked++;   // a count still on its way answers nobody
        }
    }

    public string EnhanceConfirmText
    {
        get => _enhanceConfirmText;
        private set => Set(ref _enhanceConfirmText, value);
    }

    public string EnhanceConfirmButton
    {
        get => _enhanceConfirmButton;
        private set => Set(ref _enhanceConfirmButton, value);
    }

    // Reads every account's file and hashes the portraits, under the pass gate: off the UI
    // thread. Start waits for the count - the point is to know it before saying yes.
    private void CountPictures()
    {
        var asked = ++_planAsked;
        _plan = null;
        _planChanged = false;
        StartEnhance.Enabled = false;
        EnhanceConfirmText = PassText.EnhanceCounting;
        EnhanceConfirmButton = "Turn on";
        var (level, style) = (Level(), _enhanceStyle);
        Task.Run(() => _controller.PlanEnhancement(level, style)).ContinueWith(t => Dispatcher.UIThread.Post(() =>
        {
            if (asked != _planAsked) return;
            if (t.IsFaulted)
            {
                EnhanceConfirmText = $"The pictures could not be counted: {t.Exception?.InnerException?.Message}";
                return;
            }
            _plan = t.Result;
            ShowPlan();
        }), TaskScheduler.Default);
    }

    // The question's words from the count in hand and the state as it is NOW: pausing, or the
    // first start ending, while it is open changes what it has to say.
    private void ShowPlan()
    {
        if (_plan is not { } plan) return;
        EnhanceConfirmText = PassText.EnhanceConfirm(plan, _paused, _controller.Current.Shell.FirstStart, _planChanged);
        EnhanceConfirmButton = PassText.EnhanceConfirmButton(plan);
        if (_turningOn) return;
        StartEnhance.Enabled = PassText.EnhanceConfirmable(plan);
        CancelEnhance.Enabled = true;
    }

    // The player's own way out of the question: Cancel, or the box unticked. A press on its way
    // is withdrawn in Core - which also turns it off again if the press got there first. The
    // question closing because it is on is NOT this, and never turns it off.
    private void BackOut()
    {
        if (_turningOn) _controller.WithdrawEnhance(_press);
        ConfirmEnhance = false;
    }

    // Start: on only if the count is still the one shown - checked and done in one step, in
    // Core. When it changed, it stays off and the new count is asked about.
    private void TurnOn()
    {
        if (_plan is not { } shown) return;
        var asked = ++_planAsked;
        _turningOn = true;
        StartEnhance.Enabled = false;
        CancelEnhance.Enabled = false;
        var (level, style) = (Level(), _enhanceStyle);
        var press = _press = _controller.PressEnhance();
        Task.Run(() => _controller.TurnOnEnhance(level, style, shown, press)).ContinueWith(t => Dispatcher.UIThread.Post(() =>
        {
            _turningOn = false;
            if (asked != _planAsked) return;
            CancelEnhance.Enabled = true;
            if (t.IsFaulted)
            {
                EnhanceConfirmText = $"It could not be turned on: {t.Exception?.InnerException?.Message}";
                StartEnhance.Enabled = true;
                return;
            }
            if (t.Result is { } now)
            {
                _plan = now;
                _planChanged = true;
                ShowPlan();
                return;
            }
            ConfirmEnhance = false;
            _enhance = true;
            Raise(nameof(Enhance));
        }), TaskScheduler.Default);
    }

    public bool IsWowLike { get => _enhanceStyle == EnhanceStyles.WowLike; set { if (value) SetStyle(EnhanceStyles.WowLike); } }
    public bool IsRealistic { get => _enhanceStyle == EnhanceStyles.Realistic; set { if (value) SetStyle(EnhanceStyles.Realistic); } }
    public bool IsCartoonish { get => _enhanceStyle == EnhanceStyles.Cartoonish; set { if (value) SetStyle(EnhanceStyles.Cartoonish); } }

    private void SetStyle(string style)
    {
        if (_enhanceStyle == style) return;
        _enhanceStyle = style;
        Apply();
    }

    private int Level() => int.TryParse(_enhanceMinLevel.Trim(), out var l) && l >= 1 ? l : Settings.DefaultEnhanceMinLevel;

    private void Apply()
    {
        // A press waiting behind a pass was for the settings as they were.
        if (_turningOn) _controller.WithdrawEnhance(_press);
        _controller.SetEnhance(_enhance, Level(), _enhanceStyle);
        // The level or the style changed while the question is open: the count is of another
        // batch now.
        if (_confirmEnhance) CountPictures();
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

    // ---- diagnostics: what is in the file is said BEFORE it is written
    public string DiagnosticsExplanation => Diagnostics.Explanation;

    public bool ConfirmDiagnostics
    {
        get => _confirmDiagnostics;
        set
        {
            if (!Set(ref _confirmDiagnostics, value)) return;
            if (value) DiagnosticsLine = null;
        }
    }

    public string? DiagnosticsLine
    {
        get => _diagnosticsLine;
        private set { if (Set(ref _diagnosticsLine, value)) Raise(nameof(HasDiagnosticsLine)); }
    }

    public bool HasDiagnosticsLine => _diagnosticsLine is not null;

    public bool DiagnosticsFailed
    {
        get => _diagnosticsFailed;
        private set => Set(ref _diagnosticsFailed, value);
    }

    // It reads every account's file and the cutouts: off the UI thread, one at a time.
    private void SaveDiagnosticsFile()
    {
        SaveDiagnostics.Enabled = false;
        // Downloads, not the Desktop: OneDrive syncs the Desktop by default, and the file would be uploaded.
        var downloads = Platform.NativeMethods.DownloadsFolder();
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Task.Run(() =>
        {
            try
            {
                return (Path: _controller.SaveDiagnostics(downloads, profile), Error: (string?)null);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return (Path: (string?)null, Error: ex.Message);
            }
        }).ContinueWith(t => Dispatcher.UIThread.Post(() =>
        {
            SaveDiagnostics.Enabled = true;
            ConfirmDiagnostics = false;
            var (path, error) = t.IsFaulted ? (null, t.Exception?.InnerException?.Message) : t.Result;
            DiagnosticsFailed = path is null;
            DiagnosticsLine = path is null
                ? $"The file could not be written: {error}"
                : $"Saved to your Downloads folder as {Path.GetFileName(path)}.";
            if (path is not null) Reveal(path);
        }), TaskScheduler.Default);
    }

    // ---- about, and updates
    public string VersionLine { get; }
    public string AboutText => PassText.About;
    public string BlizzardCredit => PassText.BlizzardCredit;
    public IReadOnlyList<AboutLink> AboutLinks { get; }
    public string CheckUpdatesOnOpenText => PassText.CheckUpdatesOnOpen;
    public string CheckUpdatesExplanation => PassText.CheckUpdatesExplanation;

    public bool CheckUpdatesOnOpen
    {
        get => _checkUpdatesOnOpen;
        set { if (Set(ref _checkUpdatesOnOpen, value)) _controller.SetCheckUpdatesOnOpen(value); }
    }

    public string UpdateLine => PassText.UpdateLine(_update, DateTime.Now) ?? "";
    public bool HasUpdateLine => UpdateLine.Length > 0;
    public bool UpdateFailed => _update.Stage == UpdateStage.Failed;
    /// <summary>Found, or on its way, or ready: the dashboard's footer points at Help.</summary>
    public bool HasUpdateOffer => _update.Stage is UpdateStage.Available or UpdateStage.Downloading or UpdateStage.Ready;
    public string UpdateOffer => _update.Release is { } r ? $"{r.Version.Text} is out" : "";
    public bool CanDownloadUpdate => _update.Release is not null && _update.Stage is (UpdateStage.Available or UpdateStage.Failed);
    public string DownloadUpdateText => _update.Stage == UpdateStage.Failed ? "Try again" : "Update now";
    public bool CanRestartToUpdate => _update.Stage == UpdateStage.Ready;
    public bool HasReleasePage => _update.Release is not null;

    private void ShowUpdate(Snapshot now)
    {
        var update = now.Update ?? new UpdateState();
        var was = _update;
        _update = update;
        Set(ref _checkUpdatesOnOpen, now.CheckUpdatesOnOpen, nameof(CheckUpdatesOnOpen));
        CheckUpdates.Enabled = update.Stage is not (UpdateStage.Checking or UpdateStage.Downloading or UpdateStage.Ready);
        DownloadUpdate.Enabled = CanDownloadUpdate;
        RestartToUpdate.Enabled = CanRestartToUpdate;
        if (was == update) return;
        foreach (var name in new[]
                 {
                     nameof(UpdateLine), nameof(HasUpdateLine), nameof(UpdateFailed), nameof(HasUpdateOffer), nameof(UpdateOffer),
                     nameof(CanDownloadUpdate), nameof(DownloadUpdateText), nameof(CanRestartToUpdate), nameof(HasReleasePage),
                 })
        {
            Raise(name);
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
        AltStableElsewhere = PassText.AltStableElsewhere(now.AltStableElsewhere);
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
        // The armory question, at the first start and once for an install that updated:
        // "Start watching" waits for its answer, conversion of an updated install does not.
        if (Set(ref _armory, now.Armory, nameof(Armory))) Raise(nameof(ArmoryOn));
        if (Set(ref _armorySd, now.ArmorySd, nameof(ArmorySd))) Raise(nameof(ArmoryHd));
        if (Set(ref _armoryHours, now.ArmoryRemakeHours, nameof(ArmoryRemakeAmount)) | Set(ref _armoryInDays, now.ArmoryRemakeInDays, nameof(ArmoryRemakeUnit)))
        {
            Raise(nameof(ArmoryRemakeAmount));
            Raise(nameof(ArmoryRemakeMax));
            Raise(nameof(ArmoryExplanation));
        }
        ArmorySweepText = now.ArmorySweep is { } sweep ? PassText.ArmorySweepText(sweep) : null;
        if (Set(ref _armoryAuto, now.ArmoryEnhanceAuto, nameof(ArmoryEnhanceAuto))
            | Set(ref _armoryLimit, now.ArmoryDailyLimit, nameof(ArmoryDailyLimit))
            | Set(ref _armoryMax, now.ArmoryDailyMax, nameof(ArmoryDailyMax)))
        {
            Raise(nameof(ArmoryExplanation));
        }
        ArmoryAsk = now.Armory is null && shell.Install is not null && !shell.Stopping;
        StartWatching.Enabled = now.Armory is not null;
        CheckArmory.Enabled = now.Armory == true && shell.Install is not null && !shell.Stopping && now.ArmorySweep is not { Running: true };
        if (Set(ref _blizzard, now.BlizzardClient, nameof(BlizzardSaved)))
        {
            Raise(nameof(BlizzardEntering));
            Raise(nameof(BlizzardRemovable));
            if (_blizzardNote != "Asking Battle.net...") _blizzardNote = null;
            Raise(nameof(BlizzardStatus));
        }

        // Through the fields: this is the controller telling the window, not the player.
        Set(ref _keepScreenshots, now.KeepScreenshots, nameof(KeepScreenshots));
        if (Set(ref _paused, shell.Paused, nameof(Paused))) Raise(nameof(Automatic));
        Skin = now.Skin;
        // Enhancement, from the controller: through the fields, then the derived words.
        Set(ref _enhance, now.Enhance, nameof(Enhance));
        // On already - nothing left to ask.
        if (now.Enhance && _confirmEnhance) ConfirmEnhance = false;
        if (_confirmEnhance)
        {
            // Asked before Codex had been looked for: count now that it has been.
            if (_plan is { WaitingForCodex: true } && now.CodexProbed) CountPictures();
            else ShowPlan();
        }
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
        Raise(nameof(EnhanceStatus)); Raise(nameof(HasEnhanceStatus));
        Raise(nameof(EnhanceHeld)); Raise(nameof(HasEnhanceHeld)); Raise(nameof(EnhanceHeldButton));
        ShowUpdate(now);

        _rows = PassText.WithSkipped(shell.Report?.Portraits ?? [], shell.EnhanceSkipped);
        Count = PassText.Count(_rows);
        Attention = PassText.Attention(_rows);
        GoneLine = PassText.GoneLine(_rows);
        Set(ref _hideGone, now.HideGone, nameof(HideGone));
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
        var lines = PassText.Matching(_hideGone ? [.. _rows.Where(r => !r.Gone)] : _rows, _search).Select(r =>
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

    /// <summary>A web address, to the browser. The one place the app hands a URL to the shell.</summary>
    internal static void OpenUrl(string? url)
    {
        if (string.IsNullOrEmpty(url) || !url.StartsWith("https://", StringComparison.Ordinal)) return;
        Open(url);
    }

    /// <summary>An Explorer window on the file's folder, with the file selected.</summary>
    private static void Reveal(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            // The line under the button says where it is.
        }
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
