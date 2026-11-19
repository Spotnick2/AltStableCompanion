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
}

/// <summary>A Google API answer that was not a success. The message is Google's, never a token.</summary>
public sealed class GoogleApiException(string message) : Exception(message);

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
    private const string SheetsUrl = "https://sheets.googleapis.com/v4/spreadsheets";

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

    /// <summary>A new spreadsheet with the given tabs. Returns its id and its address.</summary>
    public async Task<(string Id, string Url)> CreateAsync(string accessToken, string title, IEnumerable<string> tabs, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["properties"] = new JsonObject { ["title"] = title },
            ["sheets"] = new JsonArray(tabs.Select(t => (JsonNode)new JsonObject
            {
                ["properties"] = new JsonObject { ["title"] = t },
            }).ToArray()),
        };
        var answer = await SendAsync(HttpMethod.Post, SheetsUrl, accessToken, body, ct);
        return ((string?)answer["spreadsheetId"] ?? throw new GoogleApiException("Google: no spreadsheet id came back"),
            (string?)answer["spreadsheetUrl"] ?? "");
    }

    /// <summary>
    /// Rewrites the app's tabs whole, in one batch: a tab the player deleted is added back, and
    /// every other tab is left alone.
    /// </summary>
    public async Task WriteAsync(string accessToken, string spreadsheetId, IReadOnlyList<SheetTable> tables, CancellationToken ct)
    {
        var url = $"{SheetsUrl}/{Uri.EscapeDataString(spreadsheetId)}";
        var ids = await SheetIdsAsync(accessToken, url, ct);
        var missing = tables.Where(t => !ids.ContainsKey(t.Title)).ToList();
        if (missing.Count > 0)
        {
            var add = new JsonObject
            {
                ["requests"] = new JsonArray(missing.Select(t => (JsonNode)new JsonObject
                {
                    ["addSheet"] = new JsonObject { ["properties"] = new JsonObject { ["title"] = t.Title } },
                }).ToArray()),
            };
            await SendAsync(HttpMethod.Post, url + ":batchUpdate", accessToken, add, ct);
            ids = await SheetIdsAsync(accessToken, url, ct);
        }
        await SendAsync(HttpMethod.Post, url + ":batchUpdate", accessToken,
            new JsonObject { ["requests"] = BuildWriteRequests(tables, ids) }, ct);
    }

    /// <summary>
    /// The requests that rewrite each tab: the grid sized to the table (one spare row, so the
    /// frozen header is never every row), the header frozen, every cell written - the range is
    /// the whole tab, so a cell the table no longer has is cleared - and the columns fitted.
    /// </summary>
    internal static JsonArray BuildWriteRequests(IReadOnlyList<SheetTable> tables, IReadOnlyDictionary<string, long> sheetIds)
    {
        var requests = new JsonArray();
        foreach (var t in tables)
        {
            var id = sheetIds[t.Title];
            var columns = Math.Max(t.Columns, 1);
            requests.Add(new JsonObject
            {
                ["updateSheetProperties"] = new JsonObject
                {
                    ["properties"] = new JsonObject
                    {
                        ["sheetId"] = id,
                        ["gridProperties"] = new JsonObject
                        {
                            ["rowCount"] = t.Rows.Count + 1,
                            ["columnCount"] = columns,
                            ["frozenRowCount"] = 1,
                            ["frozenColumnCount"] = Math.Min(t.FrozenColumns, columns - 1),
                        },
                    },
                    ["fields"] = "gridProperties(rowCount,columnCount,frozenRowCount,frozenColumnCount)",
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
            requests.Add(new JsonObject
            {
                ["autoResizeDimensions"] = new JsonObject
                {
                    ["dimensions"] = new JsonObject
                    {
                        ["sheetId"] = id, ["dimension"] = "COLUMNS", ["startIndex"] = 0, ["endIndex"] = columns,
                    },
                },
            });
        }
        return requests;
    }

    private static JsonNode CellJson(SheetCell c)
    {
        var cell = new JsonObject();
        switch (c.Value)
        {
            case string s: cell["userEnteredValue"] = new JsonObject { ["stringValue"] = s }; break;
            case double d: cell["userEnteredValue"] = new JsonObject { ["numberValue"] = d }; break;
        }
        var format = new JsonObject();
        if (c.Fill is not null) format["backgroundColor"] = Color(c.Fill);
        if (c.Bold || c.Color is not null)
        {
            var text = new JsonObject();
            if (c.Bold) text["bold"] = true;
            if (c.Color is not null) text["foregroundColor"] = Color(c.Color);
            format["textFormat"] = text;
        }
        if (c.NumberFormat is not null)
            format["numberFormat"] = new JsonObject
            {
                ["type"] = c.NumberFormat.Contains('y') ? "DATE_TIME" : "NUMBER", ["pattern"] = c.NumberFormat,
            };
        if (c.Vertical) format["textRotation"] = new JsonObject { ["vertical"] = true };
        if (format.Count > 0) cell["userEnteredFormat"] = format;
        if (c.Note is not null) cell["note"] = c.Note;
        return cell;
    }

    private static JsonObject Color(string rrggbb)
    {
        double Part(int at) => int.Parse(rrggbb.AsSpan(at, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255.0;
        return new JsonObject { ["red"] = Part(0), ["green"] = Part(2), ["blue"] = Part(4) };
    }

    private async Task<Dictionary<string, long>> SheetIdsAsync(string accessToken, string url, CancellationToken ct)
    {
        var answer = await SendAsync(HttpMethod.Get, url + "?fields=sheets.properties(sheetId,title)", accessToken, null, ct);
        var ids = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var s in answer["sheets"]?.AsArray() ?? [])
        {
            if ((string?)s?["properties"]?["title"] is { } title && s["properties"]?["sheetId"] is JsonNode id)
                ids[title] = (long)id;
        }
        return ids;
    }

    private async Task<JsonNode> SendAsync(HttpMethod method, string url, string accessToken, JsonNode? body, CancellationToken ct)
    {
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
        var message = error is JsonObject e ? (string?)e["message"] : (string?)json?["error_description"] ?? (string?)error;
        throw new GoogleApiException($"Google answered {(int)response.StatusCode}: {message ?? response.ReasonPhrase}");
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
            var page = ours
                ? "<html><body style=\"font-family:sans-serif\"><h2>AltStable Companion is connected.</h2>You can close this tab.</body></html>"
                : "";
            var bytes = Encoding.UTF8.GetBytes(
                $"HTTP/1.1 {(ours ? "200 OK" : "404 Not Found")}\r\nContent-Type: text/html; charset=utf-8\r\n" +
                $"Content-Length: {Encoding.UTF8.GetByteCount(page)}\r\nConnection: close\r\n\r\n{page}");
            await stream.WriteAsync(bytes, ct);
            if (ours) return query;
        }
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
