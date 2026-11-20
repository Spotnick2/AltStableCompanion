using System.Net;
using System.Text.Json.Nodes;

namespace AltStableCompanion.Core.Tests;

/// <summary>
/// The Google Sheet export in the controller (#82, M1): Connect, Cancel, Export now, Disconnect,
/// run for real against a fake Google - the sign-in's loopback included, which a fake browser
/// sends back to as Google's page would.
/// </summary>
public sealed class SheetsControllerTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(20);
    private const string Karuzo = "Player-6796-000A598A";

    internal static string Data(TempInstall t) => Path.Combine(t.Root, "appdata");

    internal static void Until(Func<bool> happened, string what)
    {
        var limit = DateTime.UtcNow + Limit;
        while (!happened())
        {
            Assert.True(DateTime.UtcNow < limit, "never happened: " + what);
            Thread.Sleep(25);
        }
    }

    internal static TempInstall Install(Func<Settings, Settings>? settings = null)
    {
        var t = new TempInstall();
        t.WriteStore("ACCOUNT1", SheetExportTests.Db(
            SheetExportTests.Record(Karuzo, ("name", "Karuzo"), ("level", 25), ("class", "MAGE"), ("faction", "Horde"),
                ("race", "Troll"), ("sexID", 1)),
            SheetExportTests.Record("Player-6796-00000001", ("name", "Lowbie"), ("level", 5), ("faction", "Alliance"),
                ("race", "Skyborne"), ("gender", "Male"))));
        var s = new Settings { Started = true, WowFlavorDir = t.Install.FlavorDir };
        (settings?.Invoke(s) ?? s).Save(Data(t));
        return t;
    }

    private static Controller Started(TempInstall t, FakeGoogleService google, TimeSpan? signInLimit = null, bool hooks = true)
    {
        var sheets = new SheetHooks(new GoogleClient("the-client", "the-secret"), () => google)
        {
            SignInLimit = signInLimit ?? TimeSpan.FromMinutes(5),
        };
        var c = new Controller(new StartupOptions(DataDir: Data(t)), () => throw new InvalidOperationException("detection was used"),
            sheets: hooks ? sheets : null);
        c.Start();
        return c;
    }

    /// <summary>The player's browser: Google's page sends it back to the app's loopback, with a code or an error.</summary>
    private static Action<Uri> Browser(string? error = null, List<Uri>? opened = null) => uri =>
    {
        opened?.Add(uri);
        var query = GoogleSheets.ParseQuery(uri.Query.TrimStart('?'));
        var back = $"{query["redirect_uri"]}/?state={Uri.EscapeDataString(query["state"])}&"
            + (error is null ? "code=the-code" : "error=" + error);
        _ = Task.Run(async () =>
        {
            using var http = new HttpClient();
            try { await http.GetStringAsync(back); } catch (HttpRequestException) { }
        });
    };

    internal static string Exported(Controller c) =>
        Until2(() => c.Current.Sheets is { Exporting: false, Note: { } n } && !n.StartsWith("Exporting", StringComparison.Ordinal)
            ? c.Current.Sheets.Note : null, "the export's outcome");

    internal static T Until2<T>(Func<T?> got, string what) where T : class
    {
        T? value = null;
        Until(() => (value = got()) is not null, what);
        return value!;
    }

    // Karuzo's render, as the armory worker leaves it.
    private static void Drawn(TempInstall t) =>
        new CutoutFolder(t.Install.CutoutAddonDir).WriteArmory("karuzo", ArmoryFolderTests.Tga(4, 8, 9), new CutoutMeta
        {
            W = 4, H = 8, TexW = 4, TexH = 8, Guid = Karuzo, LastModified = DateTimeOffset.UtcNow.AddDays(-1).ToString("R"),
        });

    [Fact]
    public void A_build_without_the_apps_client_has_no_export()
    {
        using var t = Install();
        var google = new FakeGoogleService();
        using var c = Started(t, google, hooks: false);

        c.ConnectGoogle(Browser());
        c.ExportSheet();

        Assert.Equal(GoogleState.Unavailable, c.Current.Sheets.Google);
        Assert.Empty(google.Requests);
        Assert.Contains("Google:     not in this build", File.ReadAllText(c.SaveDiagnostics(Path.Combine(t.Root, "out"), t.Root)));
    }

    [Fact]
    public void Connecting_keeps_the_sign_in_and_exports_at_once_to_a_new_spreadsheet()
    {
        using var t = Install(s => s with { Armory = true });
        Drawn(t);
        var google = new FakeGoogleService();
        var opened = new List<Uri>();
        using (var c = Started(t, google))
        {
            Assert.Equal(GoogleState.NotConnected, c.Current.Sheets.Google);
            c.ConnectGoogle(Browser(opened: opened));

            var note = Exported(c);

            Assert.StartsWith("Exported 2 characters at ", note);
            Assert.EndsWith("The spreadsheet was made in your Google Drive." + SheetText.AllowAccess, note);
            Assert.Equal(GoogleState.Connected, c.Current.Sheets.Google);
            Assert.False(c.Current.Sheets.Problem);
            var auth = GoogleSheets.ParseQuery(Assert.Single(opened).Query.TrimStart('?'));
            Assert.Equal("the-client", auth["client_id"]);
            Assert.Equal(GoogleSheets.Scope, auth["scope"]);
            Assert.Equal("refresh-1", GoogleTokenStore.Load(Data(t)));
            var saved = Settings.Load(Data(t));
            Assert.Equal("sheet-1", saved.GoogleSheetId);
            Assert.Equal(GoogleSheets.UrlOf("sheet-1"), saved.GoogleSheetUrl);
            Assert.Equal(saved.GoogleSheetUrl, c.Current.Sheets.Url);
            Assert.Equal(["Characters", "Skills", "Reputations", "Gear"], google.Sheets["sheet-1"].Select(x => x.Title));
            // Portraits from the armory on: Karuzo's avatar, the silhouette for the level 5.
            var formulas = google.Batches.Single().ToJsonString();
            Assert.Contains(Armory.AvatarUrl(Karuzo, sd: false)!, formulas);
            Assert.Contains(Armory.PlaceholderUrl("Skyborne", "Alliance", female: false)!, formulas);
            Assert.Equal(8, System.Text.RegularExpressions.Regex.Matches(formulas, "/forever-us/").Count);    // two pictures on each of the four tabs

            // Export now: the same spreadsheet, rewritten.
            c.ExportSheet();
            Until(() => google.Batches.Count == 2, "the second export");
            Assert.StartsWith("Exported 2 characters", Exported(c));
            Assert.Equal(1, google.Created);
        }

        // The next start is connected, and diagnostics say so - never the sign-in, nor the sheet.
        using (var c = Started(t, google))
        {
            Assert.Equal(GoogleState.Connected, c.Current.Sheets.Google);
            Assert.Equal(GoogleSheets.UrlOf("sheet-1"), c.Current.Sheets.Url);
            var diagnostics = File.ReadAllText(c.SaveDiagnostics(Path.Combine(t.Root, "out"), t.Root));
            Assert.Contains("Google:     connected, a spreadsheet saved", diagnostics);
            Assert.DoesNotContain("refresh-1", diagnostics);
            Assert.DoesNotContain("sheet-1", diagnostics);
            Assert.DoesNotContain("the-secret", diagnostics);
        }
    }

    [Fact]
    public void Without_the_armory_there_are_no_avatars()
    {
        using var t = Install(s => s with { Armory = false });
        var google = new FakeGoogleService();
        using var c = Started(t, google);

        c.ConnectGoogle(Browser());
        var note = Exported(c);

        Assert.DoesNotContain("/forever-us/", google.Batches.Single().ToJsonString());
        Assert.Contains("classicon_mage", google.Batches.Single().ToJsonString());      // the icons are there
        Assert.EndsWith(SheetText.AllowAccess, note);                                  // and need Allow access
    }

    [Fact]
    public void Cancel_stops_waiting_and_connects_nothing()
    {
        using var t = Install();
        var google = new FakeGoogleService();
        using var c = Started(t, google);

        c.ConnectGoogle(_ => { });          // Google's page is open, and nobody answers it
        Assert.Equal(GoogleState.Connecting, c.Current.Sheets.Google);
        Assert.Equal(SheetText.Waiting, SheetText.State(c.Current.Sheets));
        c.CancelGoogleConnect();

        Until(() => c.Current.Sheets.Google == GoogleState.NotConnected, "back to not connected");
        Assert.Equal(SheetText.ConnectCancelled, c.Current.Sheets.Note);
        Assert.False(c.Current.Sheets.Problem);
        Assert.Null(GoogleTokenStore.Load(Data(t)));
        Assert.Empty(google.Requests);
    }

    [Fact]
    public void A_sign_in_that_never_comes_back_gives_up_and_says_why()
    {
        using var t = Install();
        var google = new FakeGoogleService();
        var limit = TimeSpan.FromMilliseconds(300);
        using var c = Started(t, google, signInLimit: limit);

        c.ConnectGoogle(_ => { });       // "Access blocked": Google never sends the browser back

        Until(() => c.Current.Sheets.Google == GoogleState.NotConnected, "the time limit");
        Assert.Equal(SheetText.ConnectTimedOut(limit), c.Current.Sheets.Note);
        Assert.True(c.Current.Sheets.Problem);
        Assert.Empty(google.Requests);
    }

    [Fact]
    public void A_permission_refused_in_the_browser_connects_nothing()
    {
        using var t = Install();
        var google = new FakeGoogleService();
        using var c = Started(t, google);

        c.ConnectGoogle(Browser(error: "access_denied"));

        Until(() => c.Current.Sheets is { Google: GoogleState.NotConnected, Note: not null }, "the refusal");
        Assert.Equal("Not connected: the permission was not given in the browser.", c.Current.Sheets.Note);
        Assert.Null(GoogleTokenStore.Load(Data(t)));
        Assert.Empty(google.Requests);
    }

    [Theory]
    [InlineData(true)]      // deleted, or another Google account's: Google answers 404
    [InlineData(false)]     // there, but none of the app's tabs left
    public void A_saved_spreadsheet_that_is_gone_is_replaced_and_the_player_told(bool deleted)
    {
        using var t = Install(s => s with { GoogleSheetId = "old", GoogleSheetUrl = GoogleSheets.UrlOf("old") });
        GoogleTokenStore.Save(Data(t), "refresh-1");
        var google = new FakeGoogleService();
        if (!deleted) google.Sheets["old"] = [new GoogleSheets.Tab(1, "Characters", null)];
        using var c = Started(t, google);

        c.ExportSheet();
        var note = Exported(c);

        Assert.Contains($"The saved spreadsheet {(deleted ? SheetText.LostSheet : SheetText.LostTabs)}, so a new one was made.", note);
        Assert.Equal("sheet-1", Settings.Load(Data(t)).GoogleSheetId);
        Assert.Equal(GoogleSheets.UrlOf("sheet-1"), c.Current.Sheets.Url);
        Assert.DoesNotContain(google.Requests, r => r.Contains("/old:batchUpdate", StringComparison.Ordinal));
    }

    [Fact]
    public void Without_its_render_on_disk_a_character_gets_the_silhouette()
    {
        using var t = Install(s => s with { Armory = true, ArmorySd = true });
        GoogleTokenStore.Save(Data(t), "refresh-1");
        var google = new FakeGoogleService();
        using var c = Started(t, google);

        c.ExportSheet();
        Exported(c);

        var formulas = google.Batches.Single().ToJsonString();
        Assert.DoesNotContain(Armory.AvatarUrl(Karuzo, sd: true)!, formulas);
        Assert.Contains(Armory.PlaceholderUrl("Troll", "Horde", female: true)!, formulas);
    }

    [Fact]
    public void Lost_settings_go_on_writing_the_spreadsheet_the_app_made_before()
    {
        using var t = Install();                                    // no spreadsheet saved
        GoogleTokenStore.Save(Data(t), "refresh-1");
        var google = new FakeGoogleService();
        google.Sheets["newer-unowned"] = [new GoogleSheets.Tab(1, "Characters", null)];      // none of our tabs: skipped
        google.Sheets["ours"] = [new GoogleSheets.Tab(1, "Characters", "Characters#1")];
        google.Order = ["newer-unowned", "ours"];
        using var c = Started(t, google);

        c.ExportSheet();
        var note = Exported(c);

        Assert.Contains(SheetText.Found, note);
        Assert.Equal(0, google.Created);
        Assert.Equal("ours", Settings.Load(Data(t)).GoogleSheetId);
        Assert.Equal(GoogleSheets.UrlOf("ours"), c.Current.Sheets.Url);
        Assert.Equal(["Characters", "Skills", "Reputations", "Gear"], google.Sheets["ours"].Select(x => x.Title));
    }

    [Fact]
    public void A_saved_spreadsheet_in_the_trash_is_left_there()
    {
        using var t = Install(s => s with { GoogleSheetId = "binned", GoogleSheetUrl = GoogleSheets.UrlOf("binned") });
        GoogleTokenStore.Save(Data(t), "refresh-1");
        var google = new FakeGoogleService();
        google.Sheets["binned"] = [new GoogleSheets.Tab(1, "Characters", "Characters#1")];
        google.Trashed.Add("binned");
        using var c = Started(t, google);

        c.ExportSheet();
        var note = Exported(c);

        Assert.Contains($"The saved spreadsheet {SheetText.LostTrashed}, so a new one was made.", note);
        Assert.DoesNotContain(google.Requests, r => r.Contains("/binned:batchUpdate", StringComparison.Ordinal));
        Assert.Equal("sheet-1", Settings.Load(Data(t)).GoogleSheetId);
    }

    [Fact]
    public void A_sign_in_google_no_longer_accepts_is_forgotten()
    {
        using var t = Install(s => s with { GoogleSheetId = "sheet-9", GoogleSheetUrl = GoogleSheets.UrlOf("sheet-9") });
        GoogleTokenStore.Save(Data(t), "refresh-1");
        var google = new FakeGoogleService { RefuseRefresh = true };
        using var c = Started(t, google);

        c.ExportSheet();
        var note = Exported(c);

        Assert.Equal(SheetText.SignedOut, note);
        Assert.True(c.Current.Sheets.Problem);
        Assert.Equal(GoogleState.NotConnected, c.Current.Sheets.Google);
        Assert.False(File.Exists(Path.Combine(Data(t), GoogleTokenStore.FileName)));
        Assert.Equal(GoogleSheets.UrlOf("sheet-9"), c.Current.Sheets.Url);     // still the player's
    }

    [Fact]
    public void An_account_that_cannot_be_read_stops_the_export_before_google_is_asked()
    {
        using var t = Install();
        // The game replacing its file: only the .bak is there.
        var bak = Path.Combine(t.Install.AccountsDir, "ACCOUNT2", "SavedVariables");
        Directory.CreateDirectory(bak);
        File.WriteAllText(Path.Combine(bak, SavedVariablesReader.FileName + ".bak"), SheetExportTests.Db());
        GoogleTokenStore.Save(Data(t), "refresh-1");
        var google = new FakeGoogleService();
        using var c = Started(t, google);

        c.ExportSheet();
        var note = Exported(c);

        Assert.StartsWith("Not exported: ACCOUNT2 could not be read just now", note);
        Assert.True(c.Current.Sheets.Problem);
        Assert.Empty(google.Requests);
    }

    [Fact]
    public void Disconnect_forgets_the_sign_in_asks_google_to_cancel_it_and_keeps_the_sheet()
    {
        using var t = Install(s => s with { GoogleSheetId = "sheet-9", GoogleSheetUrl = GoogleSheets.UrlOf("sheet-9") });
        GoogleTokenStore.Save(Data(t), "refresh-1");
        var google = new FakeGoogleService();
        using var c = Started(t, google);
        Assert.Equal(GoogleState.Connected, c.Current.Sheets.Google);

        c.DisconnectGoogle();

        Assert.False(File.Exists(Path.Combine(Data(t), GoogleTokenStore.FileName)));
        Until(() => c.Current.Sheets.Google == GoogleState.NotConnected, "disconnected");
        Assert.Equal(SheetText.Disconnected, c.Current.Sheets.Note);
        Assert.Contains("refresh-1", google.Revoked);
        Assert.Equal(GoogleSheets.UrlOf("sheet-9"), c.Current.Sheets.Url);
        Assert.Equal("sheet-9", Settings.Load(Data(t)).GoogleSheetId);
        c.ExportSheet();                                            // not connected: nothing
        Assert.DoesNotContain(google.Requests, r => r.Contains("sheets.googleapis.com", StringComparison.Ordinal));
    }

    [Fact]
    public void Disconnect_stops_the_export_in_hand_before_it_writes()
    {
        using var t = Install(s => s with { GoogleSheetId = "sheet-9", GoogleSheetUrl = GoogleSheets.UrlOf("sheet-9") });
        GoogleTokenStore.Save(Data(t), "refresh-1");
        var google = new FakeGoogleService { HoldListing = new SemaphoreSlim(0) };
        google.Sheets["sheet-9"] = [new GoogleSheets.Tab(1, "Characters", "Characters#1")];
        using var c = Started(t, google);

        c.ExportSheet();
        Until(() => google.Count("GET sheets.googleapis.com") == 1, "the export waiting on Google");
        c.DisconnectGoogle();

        Until(() => c.Current.Sheets.Google == GoogleState.NotConnected, "disconnected");
        google.HoldListing.Release();
        Assert.Equal(SheetText.Disconnected, c.Current.Sheets.Note);       // not the export's news
        Assert.False(c.Current.Sheets.Exporting);
        Thread.Sleep(200);
        Assert.Equal(0, google.Count(":batchUpdate"));
    }

    [Fact]
    public void An_export_that_ends_after_disconnect_changes_nothing()
    {
        using var t = Install(s => s with { GoogleSheetId = "sheet-9", GoogleSheetUrl = GoogleSheets.UrlOf("sheet-9") });
        GoogleTokenStore.Save(Data(t), "refresh-1");
        var google = new FakeGoogleService { HoldListing = new SemaphoreSlim(0), HoldDeaf = true };
        google.Sheets["sheet-9"] = [new GoogleSheets.Tab(1, "Characters", "Characters#1")];
        using var c = Started(t, google);

        c.ExportSheet();
        Until(() => google.Count("GET sheets.googleapis.com") == 1, "the export waiting on Google");
        c.DisconnectGoogle();
        // Disconnect waits for it 5 seconds, then goes on without it.
        Until(() => c.Current.Sheets.Google == GoogleState.NotConnected, "disconnected");
        Assert.Equal(SheetText.Disconnected, c.Current.Sheets.Note);

        // Connected again: the new export starts, and waits on Google too.
        c.ConnectGoogle(Browser());
        Until(() => google.Count("GET sheets.googleapis.com") == 2, "the new export waiting on Google");
        Assert.True(c.Current.Sheets.Exporting);

        // The old one's answer comes back: it ends, and the new export is still the one running.
        google.HoldListing.Release();
        Until(() => TestData.ReadShared(Path.Combine(Data(t), "log.txt")).Contains("google: the export was stopped"), "the old export's end");
        Thread.Sleep(100);                                  // past its last word
        Assert.True(c.Current.Sheets.Exporting);
        Assert.Equal(GoogleState.Connected, c.Current.Sheets.Google);
        Assert.Equal(0, google.Count(":batchUpdate"));      // nothing more was sent for it

        google.HoldListing.Release();
        Assert.StartsWith("Exported 2 characters", Exported(c));
        Assert.Equal(1, google.Count(":batchUpdate"));
    }

    [Fact]
    public void A_spreadsheet_made_before_a_disconnect_can_still_be_opened()
    {
        using var t = Install();                                    // nothing saved, nothing in Drive
        GoogleTokenStore.Save(Data(t), "refresh-1");
        var google = new FakeGoogleService { HoldListing = new SemaphoreSlim(0) };
        using var c = Started(t, google);

        c.ExportSheet();
        // Made, and its first write waiting on Google: then Disconnect.
        Until(() => google.Count("GET sheets.googleapis.com") == 1, "the first write waiting on Google");
        Assert.Equal(1, google.Created);
        c.DisconnectGoogle();
        Until(() => c.Current.Sheets.Google == GoogleState.NotConnected, "disconnected");

        Assert.Equal(GoogleSheets.UrlOf("sheet-1"), c.Current.Sheets.Url);
        Assert.Equal("sheet-1", Settings.Load(Data(t)).GoogleSheetId);
        Assert.Equal(0, google.Count(":batchUpdate"));
    }

    [Fact]
    public void Connect_waits_for_the_revoke_of_the_sign_in_before()
    {
        using var t = Install();
        GoogleTokenStore.Save(Data(t), "refresh-1");
        var google = new FakeGoogleService { HoldRevoke = new SemaphoreSlim(0) };
        using var c = Started(t, google);
        var opened = new List<Uri>();

        c.DisconnectGoogle();
        Until(() => google.Count("/revoke") == 1, "the revoke on its way");
        Assert.Equal(GoogleState.Disconnecting, c.Current.Sheets.Google);
        c.ConnectGoogle(Browser(opened: opened));            // too soon: nothing
        Assert.Empty(opened);
        Assert.Equal("Disconnecting...", SheetText.State(c.Current.Sheets));

        google.HoldRevoke.Release();
        Until(() => c.Current.Sheets.Google == GoogleState.NotConnected, "the revoke answered");
        c.ConnectGoogle(Browser(opened: opened));
        Until(() => c.Current.Sheets.Google == GoogleState.Connected, "connected again");
        Assert.Single(opened);
    }

    [Fact]
    public void A_rate_limited_drive_keeps_the_saved_spreadsheet()
    {
        using var t = Install(s => s with { GoogleSheetId = "sheet-9", GoogleSheetUrl = GoogleSheets.UrlOf("sheet-9") });
        GoogleTokenStore.Save(Data(t), "refresh-1");
        var google = new FakeGoogleService { DriveRateLimited = true };
        google.Sheets["sheet-9"] = [new GoogleSheets.Tab(1, "Characters", "Characters#1")];
        using var c = Started(t, google);

        c.ExportSheet();
        var note = Exported(c);

        Assert.Equal("Not exported: Google asked to slow down. Try again in a minute.", note);
        Assert.True(c.Current.Sheets.Problem);
        Assert.Equal(0, google.Created);
        Assert.DoesNotContain("GET www.googleapis.com/drive/v3/files", google.Requests);      // no search for another
        Assert.Equal("sheet-9", Settings.Load(Data(t)).GoogleSheetId);
    }

    [Fact]
    public void Every_page_of_drive_is_looked_at_before_a_new_spreadsheet_is_made()
    {
        using var t = Install();
        GoogleTokenStore.Save(Data(t), "refresh-1");
        var google = new FakeGoogleService { PageSize = 1 };
        google.Sheets["unowned"] = [new GoogleSheets.Tab(1, "Characters", null)];
        google.Sheets["ours"] = [new GoogleSheets.Tab(1, "Characters", "Characters#1")];
        google.Order = ["unowned", "ours"];
        using var c = Started(t, google);

        c.ExportSheet();
        Assert.Contains(SheetText.Found, Exported(c));

        Assert.Equal(["1"], google.PageTokens);
        Assert.Equal(0, google.Created);
        Assert.Equal("ours", Settings.Load(Data(t)).GoogleSheetId);
    }
}

/// <summary>
/// Google as the export uses it: the token and revoke endpoints, and spreadsheets made,
/// listed with the app's tags, and changed by batches of addSheet and createDeveloperMetadata.
/// A tab's tag is kept as its raw value, "Characters#1".
/// </summary>
internal sealed class FakeGoogleService : HttpMessageHandler
{
    private readonly Lock _gate = new();
    public Dictionary<string, List<GoogleSheets.Tab>> Sheets { get; } = new(StringComparer.Ordinal);
    public List<string> Requests { get; } = [];
    public List<JsonArray> Batches { get; } = [];
    public List<string> Revoked { get; } = [];
    public HashSet<string> Trashed { get; } = new(StringComparer.Ordinal);
    /// <summary>Drive's newest-first order of the app's files; by default, newest made first.</summary>
    public List<string>? Order { get; set; }
    public bool RefuseRefresh { get; set; }
    public int Created { get; private set; }
    /// <summary>Held until released: the listing of a spreadsheet's tabs, and the revoke.</summary>
    public SemaphoreSlim? HoldListing { get; set; }
    public SemaphoreSlim? HoldRevoke { get; set; }
    /// <summary>The listing's hold does not end when the request is cancelled: an answer already on its way.</summary>
    public bool HoldDeaf { get; set; }
    /// <summary>Drive's per-file answer is its 403 for a rate limit.</summary>
    public bool DriveRateLimited { get; set; }
    /// <summary>Drive's list, this many files a page.</summary>
    public int PageSize { get; set; } = 100;
    public List<string> PageTokens { get; } = [];

    public int Count(string part)
    {
        lock (_gate) return Requests.Count(r => r.Contains(part, StringComparison.Ordinal));
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var uri = request.RequestUri!;
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
        lock (_gate) Requests.Add($"{request.Method} {uri.Host}{uri.AbsolutePath}");
        if (HoldRevoke is { } revoke && uri.AbsolutePath == "/revoke") await revoke.WaitAsync(ct);
        if (HoldListing is { } listing && uri.Host == "sheets.googleapis.com" && request.Method == HttpMethod.Get)
            await listing.WaitAsync(HoldDeaf ? CancellationToken.None : ct);
        lock (_gate)
        {
            if (uri.Host == "oauth2.googleapis.com")
            {
                var form = GoogleSheets.ParseQuery(body);
                if (uri.AbsolutePath == "/revoke")
                {
                    Revoked.Add(form["token"]);
                    return Answer(new JsonObject());
                }
                if (form["client_secret"] != "the-secret") return Answer(new JsonObject { ["error"] = "invalid_client" }, HttpStatusCode.Unauthorized);
                return form["grant_type"] switch
                {
                    "authorization_code" when form["code"] == "the-code" && form.ContainsKey("code_verifier") =>
                        Answer(new JsonObject { ["refresh_token"] = "refresh-1", ["access_token"] = "access-1", ["scope"] = GoogleSheets.Scope }),
                    "refresh_token" when RefuseRefresh =>
                        Answer(new JsonObject { ["error"] = "invalid_grant", ["error_description"] = "Token has been expired or revoked." }, HttpStatusCode.BadRequest),
                    "refresh_token" when form["refresh_token"] == "refresh-1" => Answer(new JsonObject { ["access_token"] = "access-1" }),
                    _ => Answer(new JsonObject { ["error"] = "invalid_request" }, HttpStatusCode.BadRequest),
                };
            }

            if (request.Headers.Authorization?.Parameter != "access-1") return Answer(new JsonObject(), HttpStatusCode.Unauthorized);
            if (uri.Host == "www.googleapis.com")
            {
                const string files = "/drive/v3/files";
                if (uri.AbsolutePath == files)
                {
                    var order = (Order ?? [.. Sheets.Keys.Reverse()]).Where(id => Sheets.ContainsKey(id) && !Trashed.Contains(id)).ToList();
                    var query = GoogleSheets.ParseQuery(uri.Query.TrimStart('?'));
                    var from = query.TryGetValue("pageToken", out var token) ? int.Parse(token, System.Globalization.CultureInfo.InvariantCulture) : 0;
                    if (token is not null) PageTokens.Add(token);
                    var page = new JsonObject
                    {
                        ["files"] = new JsonArray(order.Skip(from).Take(PageSize).Select(id => (JsonNode)new JsonObject { ["id"] = id }).ToArray()),
                    };
                    if (from + PageSize < order.Count) page["nextPageToken"] = (from + PageSize).ToString(System.Globalization.CultureInfo.InvariantCulture);
                    return Answer(page);
                }
                var file = uri.AbsolutePath[(files.Length + 1)..];
                if (DriveRateLimited)
                    return Answer(new JsonObject
                    {
                        ["error"] = new JsonObject
                        {
                            ["code"] = 403, ["message"] = "User rate limit exceeded.",
                            ["errors"] = new JsonArray(new JsonObject { ["domain"] = "usageLimits", ["reason"] = "userRateLimitExceeded" }),
                        },
                    }, HttpStatusCode.Forbidden);
                return Sheets.ContainsKey(file)
                    ? Answer(new JsonObject { ["trashed"] = Trashed.Contains(file) })
                    : Answer(new JsonObject { ["error"] = new JsonObject { ["code"] = 404, ["message"] = "File not found." } }, HttpStatusCode.NotFound);
            }

            const string root = "/v4/spreadsheets";
            if (request.Method == HttpMethod.Post && uri.AbsolutePath == root)
            {
                var id = "sheet-" + ++Created;
                Sheets[id] = [.. JsonNode.Parse(body)!["sheets"]!.AsArray().Select(s => new GoogleSheets.Tab(
                    (long)s!["properties"]!["sheetId"]!, (string)s["properties"]!["title"]!, (string?)s["developerMetadata"]?[0]?["metadataValue"]))];
                return Answer(new JsonObject { ["spreadsheetId"] = id, ["spreadsheetUrl"] = GoogleSheets.UrlOf(id) });
            }
            var (sheet, verb) = uri.AbsolutePath[(root.Length + 1)..].Split(':') is [var a, var b] ? (a, b) : (uri.AbsolutePath[(root.Length + 1)..], "");
            if (!Sheets.TryGetValue(sheet, out var tabs))
                return Answer(new JsonObject { ["error"] = new JsonObject { ["code"] = 404, ["message"] = "Requested entity was not found." } }, HttpStatusCode.NotFound);
            if (request.Method == HttpMethod.Get)
            {
                return Answer(new JsonObject
                {
                    ["sheets"] = new JsonArray(tabs.Select(x => (JsonNode)new JsonObject
                    {
                        ["properties"] = new JsonObject { ["sheetId"] = x.Id, ["title"] = x.Title },
                        ["developerMetadata"] = x.Tag is null ? new JsonArray()
                            : new JsonArray(new JsonObject { ["metadataKey"] = GoogleSheets.TabTag, ["metadataValue"] = x.Tag }),
                    }).ToArray()),
                });
            }
            if (verb == "batchUpdate")
            {
                var requests = JsonNode.Parse(body)!["requests"]!.AsArray();
                Batches.Add(requests);
                foreach (var r in requests)
                {
                    if (r!["addSheet"]?["properties"] is { } p) tabs.Add(new GoogleSheets.Tab((long)p["sheetId"]!, (string)p["title"]!, null));
                    if (r["createDeveloperMetadata"]?["developerMetadata"] is { } m)
                    {
                        var at = tabs.FindIndex(x => x.Id == (long)m["location"]!["sheetId"]!);
                        tabs[at] = tabs[at] with { Tag = (string)m["metadataValue"]! };
                    }
                }
                return Answer(new JsonObject());
            }
            return Answer(new JsonObject(), HttpStatusCode.BadRequest);
        }
    }

    private static HttpResponseMessage Answer(JsonNode body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body.ToJsonString()) };
}
