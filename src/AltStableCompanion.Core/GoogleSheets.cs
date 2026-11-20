using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AltStableCompanion.Core;

/// <summary>
/// The app's Google OAuth client, a "Desktop app" one (#82). An installed app cannot keep a
/// secret, and Google does not treat this one as one: it is shipped, as every desktop app does.
/// </summary>
public sealed record GoogleClient(string Id, string Secret)
{
    /// <summary>The JSON the Cloud console downloads: <c>{"installed": {"client_id", "client_secret", ...}}</c>.</summary>
    public static GoogleClient FromJson(string json)
    {
        var root = JsonNode.Parse(json)?["installed"]
            ?? throw new FormatException("not a Desktop app client: no \"installed\" object");
        var id = (string?)root["client_id"];
        var secret = (string?)root["client_secret"];
        if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(secret))
            throw new FormatException("the client has no client_id or client_secret");
        return new GoogleClient(id, secret);
    }

    /// <summary>The embedded resource a build carries the client in (the Core project's <c>GoogleClientJson</c>).</summary>
    public const string ResourceName = "AltStableCompanion.google-client.json";

    /// <summary>
    /// The client this build was made with, or null: the repo never holds it. A release build
    /// embeds it from a GitHub Actions secret, the owner's deploy from their own copy; a build
    /// without it (CI, a contributor's clone) has no Google export.
    /// </summary>
    public static GoogleClient? Shipped { get; } = ReadShipped();

    private static GoogleClient? ReadShipped()
    {
        using var stream = typeof(GoogleClient).Assembly.GetManifestResourceStream(ResourceName);
        if (stream is null) return null;
        try { return FromJson(new StreamReader(stream).ReadToEnd()); }
        catch (Exception ex) when (ex is FormatException or JsonException) { return null; }
    }

    // Never the secret, in a log line or an exception that prints the record.
    public override string ToString() => $"GoogleClient {{ Id = {Id} }}";
}

/// <summary>
/// A Google API answer that was not a success. The message is Google's, never a token.
/// <see cref="Status"/> is the HTTP status; <see cref="Error"/> the token endpoint's own code
/// (<c>invalid_grant</c>: the sign-in was removed in the player's Google account, or expired).
/// </summary>
public sealed class GoogleApiException(string message, HttpStatusCode? status = null, string? error = null,
    IReadOnlyList<string>? reasons = null) : Exception(message)
{
    public HttpStatusCode? Status { get; } = status;
    public string? Error { get; } = error;

    /// <summary>The API's own reasons: <c>error.errors[].reason</c> and <c>error.status</c>.</summary>
    public IReadOnlyList<string> Reasons { get; } = reasons ?? [];

    // Drive answers 403 for its rate limits too (Codex on #84): that is "later", not "gone".
    private static readonly string[] RateReasons = ["rateLimitExceeded", "userRateLimitExceeded", "quotaExceeded", "RESOURCE_EXHAUSTED"];
    private static readonly string[] DeniedReasons = ["forbidden", "insufficientFilePermissions", "appNotAuthorizedToFile", "PERMISSION_DENIED"];

    public bool RateLimited => Status == HttpStatusCode.TooManyRequests || Reasons.Any(r => RateReasons.Contains(r));

    /// <summary>
    /// The answer says the file is not there for this sign-in: 404, or a 403 that names a
    /// permission and no rate limit. Anything else is an error to report, not a spreadsheet to replace.
    /// </summary>
    public bool NoSuchFile => Status == HttpStatusCode.NotFound
        || (Status == HttpStatusCode.Forbidden && !RateLimited && Reasons.Any(r => DeniedReasons.Contains(r)));
}

/// <summary>A saved spreadsheet with none of the app's tabs: the player chooses a new one, nothing is written.</summary>
public sealed class UnownedSheetException(string message) : Exception(message);

/// <summary>
/// The player's Google sign-in: the refresh token, kept with DPAPI for this Windows user in the
/// data folder, like <see cref="BlizzardClientStore"/>. Never shown, never logged.
/// </summary>
public static class GoogleTokenStore
{
    public const string FileName = "google-token.bin";
    private static readonly byte[] Entropy = "AltStableCompanion/GoogleToken/1"u8.ToArray();

    public static string? Load(string dataDir)
    {
        var path = Path.Combine(dataDir, FileName);
        try
        {
            if (!File.Exists(path) || !OperatingSystem.IsWindows()) return null;
            var plain = ProtectedData.Unprotect(File.ReadAllBytes(path), Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plain) is { Length: > 0 } token ? token : null;
        }
        catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException)
        {
            return null;    // unreadable: sign in again
        }
    }

    public static void Save(string dataDir, string refreshToken)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("the token is kept with Windows' DPAPI");
        Directory.CreateDirectory(dataDir);
        var path = Path.Combine(dataDir, FileName);
        File.WriteAllBytes(path + ".tmp", ProtectedData.Protect(Encoding.UTF8.GetBytes(refreshToken), Entropy, DataProtectionScope.CurrentUser));
        File.Move(path + ".tmp", path, overwrite: true);
    }

    public static void Remove(string dataDir) => File.Delete(Path.Combine(dataDir, FileName));
}

/// <summary>
/// Google sign-in and the Sheets API, by hand over <see cref="HttpClient"/> (no Google SDK).
/// Scope <c>drive.file</c> only: the app sees the spreadsheet it created and nothing else of
/// the player's Drive.
/// </summary>
public sealed class GoogleSheets(HttpClient http, GoogleClient client)
{
    public const string Scope = "https://www.googleapis.com/auth/drive.file";
    private const string AuthUrl = "https://accounts.google.com/o/oauth2/v2/auth";
    private const string TokenUrl = "https://oauth2.googleapis.com/token";
    private const string RevokeUrl = "https://oauth2.googleapis.com/revoke";
    private const string SheetsUrl = "https://sheets.googleapis.com/v4/spreadsheets";
    private const string DriveUrl = "https://www.googleapis.com/drive/v3/files";

    /// <summary>A spreadsheet's address, for "Open sheet".</summary>
    public static string UrlOf(string spreadsheetId) => $"https://docs.google.com/spreadsheets/d/{Uri.EscapeDataString(spreadsheetId)}/edit";

    /// <summary>
    /// Signs the player in: the browser opens Google's consent page, Google sends the code back
    /// to a one-shot listener on 127.0.0.1, and the code becomes a refresh token (PKCE, so a code
    /// caught on the way is useless without the verifier only this process has).
    /// </summary>
    public async Task<string> SignInAsync(Action<Uri> openBrowser, CancellationToken ct)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var redirect = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}";
            var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
            var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
            var state = Base64Url(RandomNumberGenerator.GetBytes(16));
            openBrowser(new Uri(AuthUrl + "?" + Form(
                ("client_id", client.Id), ("redirect_uri", redirect), ("response_type", "code"), ("scope", Scope),
                ("code_challenge", challenge), ("code_challenge_method", "S256"), ("state", state),
                // offline + consent: a refresh token every time, even for a player who connected before.
                ("access_type", "offline"), ("prompt", "consent"))));

            var query = await AwaitRedirectAsync(listener, state, ct);
            if (query.TryGetValue("error", out var error)) throw new GoogleApiException($"Google sign-in: {error}");
            if (!query.TryGetValue("code", out var code)) throw new GoogleApiException("Google sign-in: no code came back");

            var token = await PostTokenAsync([
                ("code", code), ("client_id", client.Id), ("client_secret", client.Secret),
                ("redirect_uri", redirect), ("grant_type", "authorization_code"), ("code_verifier", verifier)], ct);
            // Google's consent page lets the player untick a scope; without it, nothing works.
            if (!((string?)token["scope"] ?? "").Split(' ').Contains(Scope))
                throw new GoogleApiException("Google sign-in: access to the app's own spreadsheets was not granted");
            return (string?)token["refresh_token"] ?? throw new GoogleApiException("Google sign-in: no refresh token came back");
        }
        finally
        {
            listener.Stop();
        }
    }

    /// <summary>A short-lived access token from the refresh token.</summary>
    public async Task<string> AccessTokenAsync(string refreshToken, CancellationToken ct)
    {
        var token = await PostTokenAsync([
            ("refresh_token", refreshToken), ("client_id", client.Id), ("client_secret", client.Secret),
            ("grant_type", "refresh_token")], ct);
        return (string?)token["access_token"] ?? throw new GoogleApiException("Google: no access token came back");
    }

    /// <summary>
    /// The spreadsheets the app made in the player's Drive and not in the trash, newest first.
    /// With <c>drive.file</c>, Drive lists the app's own files and nothing else - renamed ones
    /// too - so a player whose settings were lost goes on writing theirs rather than getting a
    /// second one.
    /// </summary>
    public async IAsyncEnumerable<string> OwnSpreadsheetsAsync(string accessToken,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var q = Uri.EscapeDataString("mimeType='application/vnd.google-apps.spreadsheet' and trashed=false");
        string? page = null;
        do
        {
            // Every page, until the caller has one it can write (Codex on #84).
            var answer = await SendAsync(HttpMethod.Get, $"{DriveUrl}?q={q}&orderBy=modifiedTime%20desc&pageSize=100"
                + "&fields=nextPageToken,files(id)" + (page is null ? "" : "&pageToken=" + Uri.EscapeDataString(page)),
                accessToken, null, ct);
            foreach (var id in (answer["files"]?.AsArray() ?? []).Select(f => (string?)f?["id"]).OfType<string>()) yield return id;
            page = (string?)answer["nextPageToken"];
        }
        while (!string.IsNullOrEmpty(page));
    }

    /// <summary>
    /// Whether the player moved the spreadsheet to the trash: the Sheets API still writes a
    /// spreadsheet there, where nobody would see it. 404 for one deleted for good.
    /// </summary>
    public async Task<bool> TrashedAsync(string accessToken, string spreadsheetId, CancellationToken ct)
    {
        var answer = await SendAsync(HttpMethod.Get, $"{DriveUrl}/{Uri.EscapeDataString(spreadsheetId)}?fields=trashed", accessToken, null, ct);
        return (bool?)answer["trashed"] == true;
    }

    /// <summary>
    /// Tells Google to cancel the sign-in: the refresh token, and every access token it gave,
    /// stop working. "Disconnect" asks it as well as deleting the token here.
    /// </summary>
    public async Task RevokeAsync(string refreshToken, CancellationToken ct)
    {
        using var content = new FormUrlEncodedContent([new KeyValuePair<string, string>("token", refreshToken)]);
        using var response = await http.PostAsync(RevokeUrl, content, ct);
        if (!response.IsSuccessStatusCode) await ReadAsync(response, ct);
    }

    /// <summary>
    /// The developer-metadata key that marks a tab as the app's. Its value is the table's title and
    /// the id of the tab it was put on, "Gear#1320087350": a tag found on any other tab is not
    /// ours, so a copy of our tab stays the player's even if the copy carried the tag (Codex on
    /// #83). Measured 2026-11-19: duplicateSheet and sheets.copyTo do not copy it. PROJECT
    /// visibility: only this app's Cloud project sees it.
    /// </summary>
    public const string TabTag = "altstable.tab";

    /// <summary>A tab as Google lists it: its id, its title now, and the table our tag on it names - only a tag put on THIS tab.</summary>
    internal sealed record Tab(long Id, string Title, string? Tag);

    /// <summary>
    /// A new spreadsheet with one tab per title, each tagged as the app's in the same request:
    /// there is no moment when the app's tabs exist untagged. Returns its id and its address.
    /// </summary>
    public async Task<(string Id, string Url)> CreateAsync(string accessToken, string title, IEnumerable<string> tabs, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["properties"] = new JsonObject { ["title"] = title },
            ["sheets"] = new JsonArray(tabs.Select((t, i) => (JsonNode)new JsonObject
            {
                ["properties"] = new JsonObject { ["sheetId"] = i + 1, ["title"] = t },
                ["developerMetadata"] = new JsonArray(TagJson(i + 1, t)),
            }).ToArray()),
        };
        var answer = await SendAsync(HttpMethod.Post, SheetsUrl, accessToken, body, ct);
        return ((string?)answer["spreadsheetId"] ?? throw new GoogleApiException("Google: no spreadsheet id came back"),
            (string?)answer["spreadsheetUrl"] ?? "");
    }

    /// <summary>
    /// Rewrites the app's tabs whole, in one atomic batch. The app's tabs are the ones carrying
    /// its tag, never a title: a tab the player renamed is still ours, and a tab of theirs that
    /// took one of our names is still theirs. One of ours the player deleted is added back, and
    /// tagged, in the same batch that fills it - so a reply lost on the way leaves nothing for a
    /// retry to lose track of: the tag is on the sheet, not in a file here (Codex on #83).
    /// Returns the titles of the tabs it had to add.
    /// <para>
    /// The spreadsheet must hold at least one of the app's tabs - one it just created does, its
    /// tabs are tagged as they are made. One that holds none - written before the tags, or every tab of ours
    /// deleted - throws <see cref="UnownedSheetException"/> before anything is written, rather
    /// than adding a suffixed copy of every tab beside stale ones (Codex on #83).
    /// </para>
    /// </summary>
    public async Task<IReadOnlyList<string>> WriteAsync(string accessToken, string spreadsheetId,
        IReadOnlyList<SheetTable> tables, CancellationToken ct)
    {
        var url = $"{SheetsUrl}/{Uri.EscapeDataString(spreadsheetId)}";
        var listing = await SendAsync(HttpMethod.Get,
            url + "?fields=sheets(properties(sheetId,title),developerMetadata(metadataKey,metadataValue))", accessToken, null, ct);
        var tabs = Tabs(listing).ToList();
        if (!tabs.Any(t => t.Tag is not null))
            throw new UnownedSheetException("the spreadsheet holds none of AltStable Companion's tabs (made before they were tagged, or all deleted)");
        var (ids, adds) = PlanTabs(tables, tabs, () => Random.Shared.Next(1, int.MaxValue));
        var requests = new JsonArray();
        foreach (var (id, tabTitle, table) in adds)
        {
            requests.Add(new JsonObject
            {
                ["addSheet"] = new JsonObject { ["properties"] = new JsonObject { ["sheetId"] = id, ["title"] = tabTitle } },
            });
            requests.Add(new JsonObject { ["createDeveloperMetadata"] = new JsonObject { ["developerMetadata"] = TagJson(id, table) } });
        }
        foreach (var r in BuildWriteRequests(tables, ids)) requests.Add(r!.DeepClone());
        await SendAsync(HttpMethod.Post, url + ":batchUpdate", accessToken, new JsonObject { ["requests"] = requests }, ct);
        return adds.Select(a => a.Title).ToList();
    }

    /// <summary>
    /// Which tab each table goes to: the tab tagged with its title, whatever it is called now;
    /// otherwise a new tab, with
    /// a fresh id from <paramref name="nextId"/> and the table's title - or, when a tab already
    /// has that title (names are unique, whatever the case), "Title (AltStable)", "Title (AltStable 2)", ...
    /// </summary>
    internal static (Dictionary<string, long> Ids, List<(long Id, string Title, string Table)> Adds) PlanTabs(
        IReadOnlyList<SheetTable> tables, IReadOnlyList<Tab> existing, Func<int> nextId)
    {
        var ids = new Dictionary<string, long>(StringComparer.Ordinal);
        var adds = new List<(long Id, string Title, string Table)>();
        var titles = new HashSet<string>(existing.Select(e => e.Title), StringComparer.OrdinalIgnoreCase);
        var taken = new HashSet<long>(existing.Select(e => e.Id));
        foreach (var t in tables)
        {
            if (existing.FirstOrDefault(e => e.Tag == t.Title) is { } ours) { ids[t.Title] = ours.Id; continue; }
            long fresh;
            do fresh = nextId(); while (!taken.Add(fresh));
            var title = t.Title;
            for (var n = 1; titles.Contains(title); n++) title = n == 1 ? $"{t.Title} (AltStable)" : $"{t.Title} (AltStable {n})";
            titles.Add(title);
            adds.Add((fresh, title, t.Title));
            ids[t.Title] = fresh;
        }
        return (ids, adds);
    }

    private static JsonObject TagJson(long sheetId, string table) => new()
    {
        ["metadataKey"] = TabTag,
        ["metadataValue"] = $"{table}#{sheetId}",
        ["location"] = new JsonObject { ["sheetId"] = sheetId },
        ["visibility"] = "PROJECT",
    };

    internal static IEnumerable<Tab> Tabs(JsonNode answer)
    {
        foreach (var s in answer["sheets"]?.AsArray() ?? [])
        {
            if ((string?)s?["properties"]?["title"] is not { } title || s["properties"]?["sheetId"] is not JsonNode id) continue;
            var suffix = "#" + ((long)id).ToString(CultureInfo.InvariantCulture);
            var tag = (s["developerMetadata"]?.AsArray() ?? [])
                .Select(m => (string?)m?["metadataKey"] == TabTag ? (string?)m["metadataValue"] : null)
                .FirstOrDefault(v => v is not null && v.Length > suffix.Length && v.EndsWith(suffix, StringComparison.Ordinal));
            yield return new Tab((long)id, title, tag?[..^suffix.Length]);
        }
    }

    /// <summary>
    /// The requests that rewrite each tab: the grid exactly the table's size (no spare white row
    /// under the dark), its frozen rows and columns - never every one, which Sheets refuses -,
    /// no gridlines (the look is
    /// bands of colour), every cell written - the range is the whole tab, so a cell the table no
    /// longer has is cleared - each row's height (the default where the table says none, so a
    /// row that was tall is not left tall), the fixed columns' widths, and the others fitted.
    /// </summary>
    internal static JsonArray BuildWriteRequests(IReadOnlyList<SheetTable> tables, IReadOnlyDictionary<string, long> sheetIds)
    {
        var requests = new JsonArray();
        foreach (var t in tables)
        {
            var id = sheetIds[t.Title];
            var columns = Math.Max(t.Columns, 1);
            // First what the last export laid over the tab - its merges, its filter - so nothing of
            // it is left on a smaller grid, or stands in the way of shrinking it.
            requests.Add(new JsonObject { ["unmergeCells"] = new JsonObject { ["range"] = new JsonObject { ["sheetId"] = id } } });
            requests.Add(new JsonObject { ["clearBasicFilter"] = new JsonObject { ["sheetId"] = id } });
            requests.Add(new JsonObject
            {
                ["updateSheetProperties"] = new JsonObject
                {
                    ["properties"] = new JsonObject
                    {
                        ["sheetId"] = id,
                        ["gridProperties"] = new JsonObject
                        {
                            ["rowCount"] = Math.Max(t.Rows.Count, 1),
                            ["columnCount"] = columns,
                            ["frozenRowCount"] = Math.Max(0, Math.Min(t.FrozenRows, t.Rows.Count - 1)),
                            ["frozenColumnCount"] = Math.Min(t.FrozenColumns, columns - 1),
                            ["hideGridlines"] = true,
                        },
                    },
                    ["fields"] = "gridProperties(rowCount,columnCount,frozenRowCount,frozenColumnCount,hideGridlines)",
                },
            });
            requests.Add(new JsonObject
            {
                ["updateCells"] = new JsonObject
                {
                    ["range"] = new JsonObject { ["sheetId"] = id },
                    ["rows"] = new JsonArray(t.Rows.Select(r => (JsonNode)new JsonObject
                    {
                        ["values"] = new JsonArray(r.Select(CellJson).ToArray()),
                    }).ToArray()),
                    ["fields"] = "userEnteredValue,userEnteredFormat,note",
                },
            });
            // Rows: one request per run of equal heights.
            for (var from = 0; from < t.Rows.Count;)
            {
                var height = Height(t, from);
                var to = from + 1;
                while (to < t.Rows.Count && Height(t, to) == height) to++;
                requests.Add(Size(id, "ROWS", from, to, height));
                from = to;
            }
            // Columns: a fixed width, or fitted - one request per run of either.
            for (var from = 0; from < columns;)
            {
                var width = Width(t, from);
                var to = from + 1;
                while (to < columns && Width(t, to) == width) to++;
                requests.Add(width is { } px ? Size(id, "COLUMNS", from, to, px) : new JsonObject
                {
                    ["autoResizeDimensions"] = new JsonObject
                    {
                        ["dimensions"] = new JsonObject
                        {
                            ["sheetId"] = id, ["dimension"] = "COLUMNS", ["startIndex"] = from, ["endIndex"] = to,
                        },
                    },
                });
                from = to;
            }
            // Then this export's merges and filter, on the grid it just sized.
            foreach (var m in t.Merges ?? [])
                requests.Add(new JsonObject
                {
                    ["mergeCells"] = new JsonObject
                    {
                        ["range"] = Range(id, m.Row, m.Row + m.Rows, m.Column, m.Column + m.Columns),
                        ["mergeType"] = "MERGE_ALL",
                    },
                });
            if (t.Filter is { } f && f.ToRow > f.FromRow)
                requests.Add(new JsonObject
                {
                    ["setBasicFilter"] = new JsonObject
                    {
                        ["filter"] = new JsonObject { ["range"] = Range(id, f.FromRow, f.ToRow, 0, columns) },
                    },
                });
        }
        return requests;
    }

    private static JsonObject Range(long sheetId, int fromRow, int toRow, int fromColumn, int toColumn) => new()
    {
        ["sheetId"] = sheetId, ["startRowIndex"] = fromRow, ["endRowIndex"] = toRow,
        ["startColumnIndex"] = fromColumn, ["endColumnIndex"] = toColumn,
    };

    private static int Height(SheetTable t, int row) =>
        t.RowHeights is { } h && row < h.Count && h[row] is { } px ? px : DefaultRowHeight;

    private static int? Width(SheetTable t, int column) =>
        t.ColumnWidths is { } w && column < w.Count ? w[column] : null;

    /// <summary>Sheets' own row height, in pixels.</summary>
    internal const int DefaultRowHeight = 21;

    private static JsonObject Size(long sheetId, string dimension, int start, int end, int pixels) => new()
    {
        ["updateDimensionProperties"] = new JsonObject
        {
            ["range"] = new JsonObject { ["sheetId"] = sheetId, ["dimension"] = dimension, ["startIndex"] = start, ["endIndex"] = end },
            ["properties"] = new JsonObject { ["pixelSize"] = pixels },
            ["fields"] = "pixelSize",
        },
    };

    private static JsonNode CellJson(SheetCell c)
    {
        var cell = new JsonObject();
        switch (c.Value)
        {
            case string s: cell["userEnteredValue"] = new JsonObject { ["stringValue"] = s }; break;
            case double d: cell["userEnteredValue"] = new JsonObject { ["numberValue"] = d }; break;
            // A formula's string doubles its quotes; the address is ours (Armory.AvatarUrl) and has none.
            case SheetImage i: cell["userEnteredValue"] = new JsonObject { ["formulaValue"] = $"=IMAGE(\"{i.Url.Replace("\"", "\"\"")}\")" }; break;
        }
        // In the middle of its row, which is tall beside a picture; centred when it asks.
        var format = new JsonObject { ["verticalAlignment"] = "MIDDLE" };
        if (c.Center) format["horizontalAlignment"] = "CENTER";
        else if (c.Left) format["horizontalAlignment"] = "LEFT";
        if (c.Fill is not null) format["backgroundColor"] = Color(c.Fill);
        if (c.Bold || c.Color is not null || c.Link is not null || c.FontSize is not null)
        {
            var text = new JsonObject();
            if (c.Bold) text["bold"] = true;
            if (c.Color is not null) text["foregroundColor"] = Color(c.Color);
            if (c.Link is not null) text["link"] = new JsonObject { ["uri"] = c.Link };
            if (c.FontSize is { } size) text["fontSize"] = size;
            format["textFormat"] = text;
        }
        if (c.Wrap) format["wrapStrategy"] = "WRAP";
        if (c.BorderTop is { } line)
            format["borders"] = new JsonObject { ["top"] = new JsonObject { ["style"] = "SOLID", ["color"] = Color(line) } };
        if (c.NumberFormat is not null)
            format["numberFormat"] = new JsonObject
            {
                ["type"] = c.NumberFormat.Contains('y') ? "DATE_TIME" : "NUMBER", ["pattern"] = c.NumberFormat,
            };
        if (c.Vertical) format["textRotation"] = new JsonObject { ["vertical"] = true };
        cell["userEnteredFormat"] = format;
        if (c.Note is not null) cell["note"] = c.Note;
        return cell;
    }

    private static JsonObject Color(string rrggbb)
    {
        double Part(int at) => int.Parse(rrggbb.AsSpan(at, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255.0;
        return new JsonObject { ["red"] = Part(0), ["green"] = Part(2), ["blue"] = Part(4) };
    }

    private async Task<JsonNode> SendAsync(HttpMethod method, string url, string accessToken, JsonNode? body, CancellationToken ct)
    {
        // Stopped (Disconnect, Quit) between two requests: the next one is not sent, whatever
        // the handler would do with a cancelled token - a batch must not follow a Disconnect.
        ct.ThrowIfCancellationRequested();
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        if (body is not null) request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await http.SendAsync(request, ct);
        return await ReadAsync(response, ct);
    }

    private async Task<JsonNode> PostTokenAsync(IEnumerable<(string, string)> form, CancellationToken ct)
    {
        using var content = new FormUrlEncodedContent(form.Select(p => new KeyValuePair<string, string>(p.Item1, p.Item2)));
        using var response = await http.PostAsync(TokenUrl, content, ct);
        return await ReadAsync(response, ct);
    }

    // Google's error, in its own words: {"error": {"message"}} from the APIs, {"error",
    // "error_description"} from the token endpoint. Never the request, which holds the tokens.
    private static async Task<JsonNode> ReadAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var text = await response.Content.ReadAsStringAsync(ct);
        JsonNode? json = null;
        try { json = JsonNode.Parse(text); } catch (JsonException) { }
        if (response.IsSuccessStatusCode && json is not null) return json;
        var error = json?["error"];
        var code = error is JsonValue ? (string?)error : null;
        var message = error is JsonObject e ? (string?)e["message"] : (string?)json?["error_description"] ?? code;
        var reasons = error is JsonObject o
            ? (o["errors"]?.AsArray() ?? []).Select(r => (string?)r?["reason"]).Append((string?)o["status"]).OfType<string>().ToList()
            : [];
        throw new GoogleApiException($"Google answered {(int)response.StatusCode}: {message ?? response.ReasonPhrase}", response.StatusCode, code, reasons);
    }

    // The browser's redirect to http://127.0.0.1:<port>/?state=..&code=..: read each request's
    // first line, answer it, and stop at the one carrying our state. Anything else (a favicon,
    // a browser's speculative connection that never sends) is answered or dropped, not trusted.
    private static async Task<Dictionary<string, string>> AwaitRedirectAsync(TcpListener listener, string state, CancellationToken ct)
    {
        while (true)
        {
            using var tcp = await listener.AcceptTcpClientAsync(ct);
            using var stream = tcp.GetStream();
            string? line;
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(10));
                try { line = await ReadLineAsync(stream, timeout.Token); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { continue; }
            }
            var target = line?.Split(' ') is [_, var t, ..] ? t : "";
            var query = ParseQuery(target.Contains('?') ? target[(target.IndexOf('?') + 1)..] : "");
            var ours = query.TryGetValue("state", out var s) && s == state;
            var page = ours ? CallbackPage(query) : "";
            var bytes = Encoding.UTF8.GetBytes(
                $"HTTP/1.1 {(ours ? "200 OK" : "404 Not Found")}\r\nContent-Type: text/html; charset=utf-8\r\n" +
                $"Content-Length: {Encoding.UTF8.GetByteCount(page)}\r\nConnection: close\r\n\r\n{page}");
            await stream.WriteAsync(bytes, ct);
            if (ours) return query;
        }
    }

    // What the browser shows once Google sends it back. Not "connected": the code still has to
    // become a token, and the scope still has to be checked - the app says how that went. An
    // error (the player refused, or is not a test user) says so (Codex on #83).
    internal static string CallbackPage(IReadOnlyDictionary<string, string> query)
    {
        var (title, text) = query.TryGetValue("error", out var error)
            ? ("Google did not connect AltStable Companion", $"Google answered: {WebUtility.HtmlEncode(error)}. You can close this tab.")
            : ("Received", "You can close this tab: AltStable Companion says whether the connection worked.");
        return $"<html><body style=\"font-family:sans-serif\"><h2>{title}</h2>{text}</body></html>";
    }

    private static async Task<string?> ReadLineAsync(NetworkStream stream, CancellationToken ct)
    {
        var buffer = new List<byte>();
        var one = new byte[1];
        while (buffer.Count < 8192)
        {
            if (await stream.ReadAsync(one, ct) == 0) break;
            if (one[0] == '\n') break;
            if (one[0] != '\r') buffer.Add(one[0]);
        }
        return buffer.Count == 0 ? null : Encoding.ASCII.GetString(buffer.ToArray());
    }

    internal static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            var key = Uri.UnescapeDataString((eq < 0 ? pair : pair[..eq]).Replace('+', ' '));
            result[key] = eq < 0 ? "" : Uri.UnescapeDataString(pair[(eq + 1)..].Replace('+', ' '));
        }
        return result;
    }

    private static string Form(params (string Key, string Value)[] pairs) =>
        string.Join('&', pairs.Select(p => Uri.EscapeDataString(p.Key) + "=" + Uri.EscapeDataString(p.Value)));

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
