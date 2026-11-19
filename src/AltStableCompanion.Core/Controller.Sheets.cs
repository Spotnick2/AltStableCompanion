using System.Globalization;
using System.Net;

namespace AltStableCompanion.Core;

/// <summary>
/// The way to Google for the sheet export (#82): the app's own OAuth client and the network. A
/// controller made without it has no export; the app hands in the shipped client, when the build
/// has one, and tests a fake.
/// </summary>
public sealed record SheetHooks(GoogleClient Client, Func<HttpMessageHandler> Http)
{
    /// <summary>How long Connect waits for the browser to come back before it gives up.</summary>
    public TimeSpan SignInLimit { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>The real thing, or null for a build that carries no client.</summary>
    public static SheetHooks? Real() =>
        GoogleClient.Shipped is { } client ? new(client, () => new SocketsHttpHandler()) : null;
}

public enum GoogleState
{
    /// <summary>This build has no Google client: the section is not shown.</summary>
    Unavailable,
    NotConnected,
    /// <summary>The browser is open on Google's page; Cancel stops waiting.</summary>
    Connecting,
    Connected,
}

/// <summary>
/// Where the sheet export stands. <see cref="Note"/> is what the last Connect, Export or
/// Disconnect came to (null for nothing to say); <see cref="Problem"/> when it went wrong.
/// <see cref="Url"/> is the saved spreadsheet's address, for "Open sheet".
/// </summary>
public sealed record SheetState(GoogleState Google = GoogleState.Unavailable, bool Exporting = false,
    string? Url = null, string? Note = null, bool Problem = false);

public sealed partial class Controller
{
    /// <summary>The spreadsheet's name in the player's Drive.</summary>
    public const string SheetTitle = "AltStable";

    private static readonly TimeSpan GoogleTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan RevokeTimeout = TimeSpan.FromSeconds(15);

    private string? _googleToken;                        // under _gate: the refresh token, never shown or logged
    private CancellationTokenSource? _connectCts;        // under _gate: a sign-in waiting for the browser
    private bool _connectCancelled;                      // under _gate: by the player, not by the time limit
    private readonly CancellationTokenSource _sheetsStop = new();
    private Task? _connectTask;
    private Task? _exportTask;

    private void StartSheets()
    {
        if (sheets is null) return;
        var token = GoogleTokenStore.Load(_dataDir);
        lock (_gate)
        {
            _googleToken = token;
            _current = _current with
            {
                Sheets = new SheetState(token is null ? GoogleState.NotConnected : GoogleState.Connected, Url: _settings.GoogleSheetUrl),
            };
        }
    }

    private void StopSheets()
    {
        try { _sheetsStop.Cancel(); } catch (ObjectDisposedException) { }
    }

    private async Task WaitForSheetsAsync()
    {
        // A batch is atomic on Google's side: one cut short has written nothing.
        foreach (var task in new[] { _connectTask, _exportTask })
            if (task is not null) await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(5)));
    }

    /// <summary>
    /// "Connect": Google's page opens in the browser, and the app waits - at most
    /// <see cref="SheetHooks.SignInLimit"/>, or until Cancel - for it to send the player back.
    /// Connected, it exports at once: the player connected to have the sheet.
    /// </summary>
    public void ConnectGoogle(Action<Uri> openBrowser)
    {
        if (sheets is not { } hooks) return;
        CancellationTokenSource cts;
        lock (_gate)
        {
            if (_stopping || _current.Sheets.Google != GoogleState.NotConnected) return;
            cts = CancellationTokenSource.CreateLinkedTokenSource(_sheetsStop.Token);
            cts.CancelAfter(hooks.SignInLimit);
            _connectCts = cts;
            _connectCancelled = false;
            _current = _current with { Sheets = _current.Sheets with { Google = GoogleState.Connecting, Note = null, Problem = false } };
        }
        Changed?.Invoke();
        _connectTask = Task.Run(() => ConnectAsync(hooks, openBrowser, cts));
    }

    /// <summary>"Cancel" while the browser is open: stop waiting for Google.</summary>
    public void CancelGoogleConnect()
    {
        CancellationTokenSource? cts;
        lock (_gate)
        {
            cts = _connectCts;
            if (cts is not null) _connectCancelled = true;
        }
        try { cts?.Cancel(); } catch (ObjectDisposedException) { }
    }

    private async Task ConnectAsync(SheetHooks hooks, Action<Uri> openBrowser, CancellationTokenSource cts)
    {
        string? token = null;
        string? note = null;
        var problem = true;
        try
        {
            using var http = new HttpClient(hooks.Http()) { Timeout = GoogleTimeout };
            token = await new GoogleSheets(http, hooks.Client).SignInAsync(openBrowser, cts.Token);
            problem = false;
            try
            {
                GoogleTokenStore.Save(_dataDir, token);
                _log?.Write("google: connected");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                or System.Security.Cryptography.CryptographicException or PlatformNotSupportedException)
            {
                note = SheetText.NotSaved;
                _log?.Write($"google: connected, but the sign-in could not be saved: {ex.GetType().Name}");
            }
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            if (_sheetsStop.IsCancellationRequested) return;      // quitting: nothing to say
            bool byPlayer;
            lock (_gate) byPlayer = _connectCancelled;
            note = byPlayer ? SheetText.ConnectCancelled : SheetText.ConnectTimedOut(hooks.SignInLimit);
            problem = !byPlayer;
            _log?.Write(byPlayer ? "google: connecting cancelled" : "google: connecting timed out");
        }
        catch (Exception ex)
        {
            // Google's words, or the network's: never a token, which only the request holds.
            note = SheetText.ConnectFailed(ex);
            _log?.Write($"google: could not connect: {ex.GetType().Name}: {ex.Message}");
        }

        lock (_gate)
        {
            if (_connectCts == cts) _connectCts = null;
            if (token is not null) _googleToken = token;
            _current = _current with
            {
                Sheets = _current.Sheets with
                {
                    Google = token is null ? GoogleState.NotConnected : GoogleState.Connected,
                    Note = note,
                    Problem = problem,
                },
            };
        }
        cts.Dispose();
        Changed?.Invoke();
        if (token is not null) ExportSheet();
    }

    /// <summary>
    /// "Disconnect": the sign-in is deleted here and Google is asked to cancel it. The
    /// spreadsheet stays in the player's Drive, and its address here: the same account,
    /// connected again, goes on writing it. While the browser is open, it is Cancel.
    /// </summary>
    public void DisconnectGoogle()
    {
        if (sheets is not { } hooks) return;
        string? token = null;
        bool connecting;
        lock (_gate)
        {
            connecting = _current.Sheets.Google == GoogleState.Connecting;
            if (!connecting) (token, _googleToken) = (_googleToken, null);
        }
        if (connecting)
        {
            CancelGoogleConnect();
            return;
        }

        string? problem = null;
        try
        {
            GoogleTokenStore.Remove(_dataDir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            problem = SheetText.NotDeleted;
            _log?.Write($"google: the sign-in's file could not be deleted: {ex.GetType().Name}");
        }
        lock (_gate)
        {
            _current = _current with
            {
                Sheets = _current.Sheets with { Google = GoogleState.NotConnected, Note = problem ?? SheetText.Disconnected, Problem = problem is not null },
            };
        }
        _log?.Write("google: disconnected");
        Changed?.Invoke();
        if (token is not null) _ = RevokeAsync(hooks, token);
    }

    // Best effort: the token is gone from this PC whatever Google says.
    private async Task RevokeAsync(SheetHooks hooks, string token)
    {
        try
        {
            using var http = new HttpClient(hooks.Http()) { Timeout = RevokeTimeout };
            await new GoogleSheets(http, hooks.Client).RevokeAsync(token, CancellationToken.None);
            _log?.Write("google: the sign-in was cancelled at Google");
        }
        catch (Exception ex)
        {
            _log?.Write($"google: Google could not be asked to cancel the sign-in: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// "Export now": every account's roster, written to the saved spreadsheet - or to a new one,
    /// when there is none yet, or the saved one is gone. Nothing is written unless every account
    /// was read: a missing one would drop its characters, and could show one it hides.
    /// </summary>
    public void ExportSheet()
    {
        if (sheets is not { } hooks) return;
        string token;
        WowInstall install;
        bool armory, sd;
        string? sheetId;
        lock (_gate)
        {
            if (_stopping || _current.Sheets is not { Google: GoogleState.Connected, Exporting: false } || _googleToken is not { } held) return;
            if (_current.Shell.Install is not { } i)
            {
                _current = _current with { Sheets = _current.Sheets with { Note = SheetText.NoInstall, Problem = true } };
                Changed?.Invoke();
                return;
            }
            (token, install) = (held, i);
            armory = _settings.Armory == true;
            sd = _settings.ArmorySd;
            sheetId = _settings.GoogleSheetId;
            _current = _current with { Sheets = _current.Sheets with { Exporting = true, Note = null, Problem = false } };
        }
        Changed?.Invoke();
        _exportTask = Task.Run(() => ExportAsync(hooks, token, install, armory, sd, sheetId));
    }

    private async Task ExportAsync(SheetHooks hooks, string token, WowInstall install, bool armory, bool sd, string? sheetId)
    {
        string note;
        var problem = true;
        string? url = null;
        var signedOut = false;
        var stop = _sheetsStop.Token;
        try
        {
            var roster = SheetExport.Load(install.AccountsDir);
            if (roster.NoAccounts)
            {
                note = SheetText.NoAccounts;
            }
            else if (!roster.Complete)
            {
                note = SheetText.Skipped(roster.Skipped.Select(AccountOf));
            }
            else
            {
                var now = _clock();
                // The renders the app holds: an avatar for those, the silhouette for everyone else.
                var avatars = armory ? new AvatarSource(sd, new HashSet<string>(
                    new CutoutFolder(install.CutoutAddonDir).ArmoryRenders().Keys, StringComparer.Ordinal)) : null;
                var tables = SheetExport.Build(roster.Characters, new DateTimeOffset(now).ToUnixTimeSeconds(), avatars);
                using var http = new HttpClient(hooks.Http()) { Timeout = GoogleTimeout };
                var google = new GoogleSheets(http, hooks.Client);
                var access = await google.AccessTokenAsync(token, stop);

                string? lost = null;                     // why the saved spreadsheet was not used
                IReadOnlyList<string>? added = null;     // null until a spreadsheet took the export
                if (sheetId is not null)
                {
                    (added, lost) = await TryWriteAsync(google, access, sheetId, tables, stop, checkTrash: true);
                }
                // None saved, or it is gone: the app's own spreadsheet, if the player has one -
                // one spreadsheet, not one more each time the settings are lost.
                var found = false;
                if (added is null)
                {
                    foreach (var id in await google.OwnSpreadsheetsAsync(access, stop))
                    {
                        if (id == sheetId) continue;
                        (added, _) = await TryWriteAsync(google, access, id, tables, stop, checkTrash: false);
                        if (added is null) continue;
                        Save(s => s with { GoogleSheetId = id, GoogleSheetUrl = GoogleSheets.UrlOf(id) }, quiet: true);
                        url = GoogleSheets.UrlOf(id);
                        found = true;
                        _log?.Write("google: went on writing a spreadsheet the app made before");
                        break;
                    }
                }
                var made = false;
                if (added is null)
                {
                    var (id, address) = await google.CreateAsync(access, SheetTitle, tables.Select(t => t.Title), stop);
                    // Kept before it is filled: a write that fails now must not make another one next time.
                    Save(s => s with { GoogleSheetId = id, GoogleSheetUrl = address }, quiet: true);
                    url = address;
                    made = true;
                    _log?.Write(lost is null ? "google: a spreadsheet was made" : $"google: the saved spreadsheet {lost}; a new one was made");
                    added = await google.WriteAsync(access, id, tables, stop);
                }
                note = SheetText.Exported(roster.Characters.Count, now, made, lost, added, avatars: armory, found: found);
                problem = false;
                _log?.Write($"google: exported {roster.Characters.Count} characters"
                    + (added.Count > 0 ? $"; added back {string.Join(", ", added)}" : ""));
            }
        }
        catch (GoogleApiException ex) when (ex.Error == "invalid_grant")
        {
            signedOut = true;
            note = SheetText.SignedOut;
            _log?.Write("google: Google no longer accepts the sign-in");
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            return;     // quitting
        }
        catch (Exception ex)
        {
            note = SheetText.ExportFailed(ex);
            _log?.Write($"google: the export failed: {ex.GetType().Name}: {ex.Message}");
        }

        if (signedOut)
        {
            try { GoogleTokenStore.Remove(_dataDir); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        lock (_gate)
        {
            // Signed out by Google: only if this is still the sign-in it refused.
            var refused = signedOut && _googleToken == token;
            if (refused) _googleToken = null;
            _current = _current with
            {
                Sheets = _current.Sheets with
                {
                    Google = refused ? GoogleState.NotConnected : _current.Sheets.Google,
                    Exporting = false,
                    Url = url ?? _current.Sheets.Url,
                    Note = note,
                    Problem = problem,
                },
            };
        }
        Changed?.Invoke();
    }

    // The export, into this spreadsheet: the tabs it added back, or null and why it was not used.
    private static async Task<(IReadOnlyList<string>? Added, string? Lost)> TryWriteAsync(GoogleSheets google, string access,
        string id, IReadOnlyList<SheetTable> tables, CancellationToken stop, bool checkTrash)
    {
        try
        {
            if (checkTrash && await google.TrashedAsync(access, id, stop)) return (null, SheetText.LostTrashed);
            return (await google.WriteAsync(access, id, tables, stop), null);
        }
        catch (UnownedSheetException)
        {
            return (null, SheetText.LostTabs);
        }
        catch (GoogleApiException ex) when (ex.Status is HttpStatusCode.NotFound or HttpStatusCode.Forbidden)
        {
            return (null, SheetText.LostSheet);
        }
    }

    // ...\WTF\Account\<account>\SavedVariables\AltStable.lua -> <account>
    private static string AccountOf(string storePath) =>
        Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(storePath))) ?? storePath;
}

/// <summary>What the Google Sheet section says. Every word about what is sent is exact.</summary>
public static class SheetText
{
    public const string Explanation =
        "Off until you connect. Connect opens Google's sign-in page in your browser, and the app asks for one permission: "
        + "the spreadsheets it creates itself. It cannot see anything else in your Google Drive. Export now writes every "
        + "character AltStable has, except those you hid, into a spreadsheet named \"AltStable\" in your Drive: name, realm, "
        + "class, race, faction, level, item level, rested XP, gold, guild, when it was last played, its gear and its skills. "
        + "The app talks to oauth2.googleapis.com to sign in, sheets.googleapis.com to write, and www.googleapis.com (Drive) "
        + "to find the spreadsheet it made before and whether it is in the trash, only when you press Connect, "
        + "Export now or Disconnect.With portraits from the armory on, the Characters tab also shows each character's armory picture "
        + "(its silhouette until the armory has drawn it): Google's servers fetch it from Blizzard's, once you open the spreadsheet and choose "
        + "Allow access, which Google asks for once. The sign-in is kept for this Windows user only. "
        + "Disconnect deletes it and asks Google to cancel it; the spreadsheet stays yours. Tabs you add to the spreadsheet "
        + "are never touched.";

    public const string Waiting = "Waiting for Google in your browser: sign in and allow it there. Cancel stops waiting.";
    public const string Exporting = "Exporting...";
    public const string ConnectCancelled = "Not connected: cancelled.";
    public const string Disconnected = "Disconnected. The spreadsheet stays in your Google Drive.";
    public const string NotSaved = "Connected for this run only: the sign-in could not be saved, so Connect is needed again after a restart.";
    public const string NotDeleted = "Disconnected, but the sign-in's file (google-token.bin, in the app's data folder) could not be deleted. Delete it yourself.";
    public const string NoInstall = "Not exported: no World of Warcraft folder is set.";
    public const string NoAccounts = "Not exported: no account in this World of Warcraft folder uses AltStable yet.";
    public const string SignedOut = "Google no longer accepts this connection: it was removed in your Google account, or it expired. Connect again.";
    public const string LostSheet = "could not be found (deleted, or made with another Google account)";
    public const string LostTabs = "has none of the app's tabs left";
    public const string LostTrashed = "is in the trash";
    public const string Found = " It went on writing the spreadsheet it made before.";

    /// <summary>The line under the buttons when nothing has just happened.</summary>
    public static string State(SheetState state) => state switch
    {
        { Note: { } note } => note,
        { Google: GoogleState.Connecting } => Waiting,
        { Exporting: true } => Exporting,
        { Google: GoogleState.Connected, Url: null } => "Connected. Export now makes the spreadsheet in your Google Drive.",
        { Google: GoogleState.Connected } => "Connected.",
        _ => "Not connected.",
    };

    public static string ConnectTimedOut(TimeSpan limit) =>
        $"Not connected: Google did not send the browser back within {limit.TotalMinutes:0} minutes. "
        + "If Google said the app is blocked, this Google account cannot use it yet.";

    public static string ConnectFailed(Exception ex) => ex switch
    {
        GoogleApiException { Message: var m } when m.Contains("access_denied", StringComparison.Ordinal) =>
            "Not connected: the permission was not given in the browser.",
        GoogleApiException g => "Not connected: " + g.Message,
        HttpRequestException or TaskCanceledException => "Not connected: Google could not be reached.",
        _ => "Not connected: " + ex.Message,
    };

    public static string ExportFailed(Exception ex) => ex switch
    {
        GoogleApiException g => "Not exported: " + g.Message,
        HttpRequestException or TaskCanceledException => "Not exported: Google could not be reached.",
        _ => "Not exported: " + ex.Message,
    };

    public static string Skipped(IEnumerable<string> accounts) =>
        $"Not exported: {string.Join(", ", accounts)} could not be read just now (the game may be writing it). Try again in a moment.";

    /// <summary>Measured 2026-11-19: an =IMAGE() of another site shows #REF! until the owner allows it, once per spreadsheet.</summary>
    public const string AllowAccess = " For the armory pictures to show, open it and choose Allow access.";

    public static string Exported(int characters, DateTime at, bool made, string? lost, IReadOnlyList<string> added,
        bool avatars = false, bool found = false)
    {
        var text = string.Create(CultureInfo.InvariantCulture,
            $"Exported {characters} character{(characters == 1 ? "" : "s")} at {at:HH:mm}.");
        if (made) text += (lost is null ? " The spreadsheet was made in your Google Drive." : $" The saved spreadsheet {lost}, so a new one was made.")
            + (avatars ? AllowAccess : "");
        else if (found) text += (lost is null ? "" : $" The saved spreadsheet {lost}.") + Found;
        if (added.Count > 0) text += $" Put back: {string.Join(", ", added)}.";
        return text;
    }
}
