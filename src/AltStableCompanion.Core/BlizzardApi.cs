using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AltStableCompanion.Core;

/// <summary>The player's own Battle.net API client (#63, M3). A shipped app cannot hold a secret.</summary>
public sealed record BlizzardClient(string Id, string Secret);

/// <summary>
/// The client, kept with DPAPI for this Windows user in the data folder. Never shown again once
/// saved, never logged, never in diagnostics. A file this user cannot decrypt (a data folder
/// copied from another Windows account) reads as unreadable: "enter your client again", and the
/// armory carries on without it.
/// </summary>
public static class BlizzardClientStore
{
    public const string FileName = "blizzard-client.bin";
    private static readonly byte[] Entropy = "AltStableCompanion/BlizzardClient/1"u8.ToArray();

    public static string PathIn(string dataDir) => Path.Combine(dataDir, FileName);

    /// <summary>The client, or null: none saved, or <paramref name="unreadable"/>.</summary>
    public static BlizzardClient? Load(string dataDir, out bool unreadable)
    {
        unreadable = false;
        var path = PathIn(dataDir);
        try
        {
            if (!File.Exists(path)) return null;
            if (!OperatingSystem.IsWindows()) { unreadable = true; return null; }
            var plain = ProtectedData.Unprotect(File.ReadAllBytes(path), Entropy, DataProtectionScope.CurrentUser);
            var saved = JsonSerializer.Deserialize<Saved>(plain);
            if (saved is { Id: { Length: > 0 } id, Secret: { Length: > 0 } secret }) return new BlizzardClient(id, secret);
            unreadable = true;
            return null;
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException or IOException or UnauthorizedAccessException)
        {
            unreadable = true;
            return null;
        }
    }

    public static void Save(string dataDir, BlizzardClient client)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("the client is kept with Windows' DPAPI");
        Directory.CreateDirectory(dataDir);
        var plain = JsonSerializer.SerializeToUtf8Bytes(new Saved(client.Id, client.Secret));
        var path = PathIn(dataDir);
        var tmp = path + ".tmp";
        File.WriteAllBytes(tmp, ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser));
        File.Move(tmp, path, overwrite: true);
    }

    public static void Remove(string dataDir)
    {
        var path = PathIn(dataDir);
        if (File.Exists(path)) File.Delete(path);
    }

    private sealed record Saved(string Id, string Secret);
}

public enum BlizzardStatus
{
    /// <summary>A profile, of THIS character (its id is the GUID's player number).</summary>
    Ok,
    /// <summary>404, or a profile of someone else (a rename, a namesake): no API data.</summary>
    NotFound,
    /// <summary>The client was refused (a bad id or secret, a 401 after a new token): the API is off for this run.</summary>
    Refused,
    /// <summary>429 or a 5xx: everyone waits, as for the CDN.</summary>
    Busy,
    /// <summary>Anything else. Said in the log, tried again later.</summary>
    Failed,
    /// <summary>Not sent: by the time it could be, it was no longer allowed (hidden, the client removed, the armory off) or the client changed. Nothing to keep.</summary>
    NotAsked,
}

/// <param name="LastLogin">The armory's <c>last_login_timestamp</c>: play this PC may not have seen.</param>
public sealed record BlizzardProfile(int Level, DateTimeOffset? LastLogin);

public sealed record BlizzardAnswer(BlizzardStatus Status, BlizzardProfile? Profile = null, TimeSpan? RetryAfter = null, string? Problem = null);

/// <summary>
/// The profile API with the player's own client: a client-credentials token from
/// <c>oauth.battle.net</c>, kept in memory until it expires, and one profile GET per character
/// on <c>us.api.blizzard.com</c>. The id and secret go only to the token host and the token only
/// to the API host; neither is ever in a problem text. No redirects, 30 seconds, 1 MB.
/// </summary>
public sealed class BlizzardApi(Func<HttpMessageHandler> handler, string version, Func<DateTimeOffset> now)
{
    public const string TokenHost = "oauth.battle.net";
    public const string ApiHost = "us.api.blizzard.com";
    public const string Namespace = "profile-forever-us";
    public const int MaxBytes = 1024 * 1024;
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private readonly Lock _lock = new();
    private string? _token;
    private DateTimeOffset _expires;
    private BlizzardClient? _tokenFor;
    private int _epoch;              // under _lock: a Forget while a token is asked for drops that token

    /// <summary>
    /// The name in the profile path: the full name, surname too, lower-case, a space as "-"
    /// (measured: <c>karuzo-macphisto</c>, no realm). Escaped as one path segment.
    /// </summary>
    public static string Slug(string name) =>
        Uri.EscapeDataString(name.Trim().ToLowerInvariant().Replace(' ', '-'));

    /// <summary>Drops the token: a client removed or replaced.</summary>
    public void Forget()
    {
        lock (_lock) { _token = null; _tokenFor = null; _epoch++; }
    }

    /// <summary>"Test": a token for this client, or why not.</summary>
    public async Task<BlizzardAnswer> TestAsync(BlizzardClient client, CancellationToken ct)
    {
        Forget();
        var (token, answer) = await TokenAsync(client, ct);
        return token is null ? answer! : new BlizzardAnswer(BlizzardStatus.Ok);
    }

    /// <summary>
    /// The character's profile, only when its id is <paramref name="playerNumber"/>. A 401 gets
    /// one new token and one more try; a second 401 is a refusal. <paramref name="mayAsk"/> is
    /// asked right before each request that names the character, after any wait for a token:
    /// false, and nothing is sent.
    /// </summary>
    public async Task<BlizzardAnswer> ProfileAsync(BlizzardClient client, string name, long playerNumber, Func<bool> mayAsk, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            var (token, failed) = await TokenAsync(client, ct);
            if (token is null) return failed!;
            if (!mayAsk()) return new BlizzardAnswer(BlizzardStatus.NotAsked);
            var url = $"https://{ApiHost}/profile/wow/character/{Slug(name)}?namespace={Namespace}&locale=en_US";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var (code, body, retry, problem) = await SendAsync(request, ct);
            if (problem is not null) return new BlizzardAnswer(BlizzardStatus.Failed, Problem: problem);
            if (code == 401)
            {
                Forget();
                if (attempt == 0) continue;
                return new BlizzardAnswer(BlizzardStatus.Refused, Problem: "the API refused the client's token");
            }
            if (code == 404) return new BlizzardAnswer(BlizzardStatus.NotFound);
            if (code == 429 || code >= 500) return new BlizzardAnswer(BlizzardStatus.Busy, RetryAfter: retry, Problem: $"the API answered {code}");
            if (code != 200) return new BlizzardAnswer(BlizzardStatus.Failed, Problem: $"the API answered {code}");
            return Parse(body!, playerNumber);
        }
    }

    private static BlizzardAnswer Parse(byte[] body, long playerNumber)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            // Someone else's profile under this name: nothing in it is about this character.
            if (!root.TryGetProperty("id", out var id) || !id.TryGetInt64(out var n) || n != playerNumber)
            {
                return new BlizzardAnswer(BlizzardStatus.NotFound);
            }
            var level = root.TryGetProperty("level", out var l) && l.TryGetInt32(out var lv) ? lv : 0;
            DateTimeOffset? login = root.TryGetProperty("last_login_timestamp", out var t) && t.TryGetInt64(out var ms) && ms > 0
                ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : null;
            return new BlizzardAnswer(BlizzardStatus.Ok, new BlizzardProfile(level, login));
        }
        catch (Exception ex) when (ex is JsonException or ArgumentOutOfRangeException or InvalidOperationException)
        {
            return new BlizzardAnswer(BlizzardStatus.Failed, Problem: "the API's answer could not be read");
        }
    }

    // A token for this client: the one held, or a new one.
    private async Task<(string? Token, BlizzardAnswer? Failed)> TokenAsync(BlizzardClient client, CancellationToken ct)
    {
        int epoch;
        lock (_lock)
        {
            if (_token is not null && _tokenFor == client && now() < _expires) return (_token, null);
            epoch = _epoch;
        }
        using var request = new HttpRequestMessage(HttpMethod.Post, $"https://{TokenHost}/token")
        {
            Content = new FormUrlEncodedContent([new("grant_type", "client_credentials")]),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes(Uri.EscapeDataString(client.Id) + ":" + Uri.EscapeDataString(client.Secret))));
        var (code, body, retry, problem) = await SendAsync(request, ct);
        if (problem is not null) return (null, new BlizzardAnswer(BlizzardStatus.Failed, Problem: problem));
        if (code is 400 or 401 or 403) return (null, new BlizzardAnswer(BlizzardStatus.Refused, Problem: "Battle.net refused the client id or secret"));
        if (code == 429 || code >= 500) return (null, new BlizzardAnswer(BlizzardStatus.Busy, RetryAfter: retry, Problem: $"Battle.net answered {code}"));
        if (code != 200) return (null, new BlizzardAnswer(BlizzardStatus.Failed, Problem: $"Battle.net answered {code}"));
        try
        {
            using var doc = JsonDocument.Parse(body!);
            var token = doc.RootElement.GetProperty("access_token").GetString();
            var seconds = doc.RootElement.TryGetProperty("expires_in", out var e) && e.TryGetInt64(out var s) ? s : 0;
            if (string.IsNullOrEmpty(token)) throw new JsonException();
            lock (_lock)
            {
                // The client was removed or replaced while this was asked for: not its token.
                if (epoch != _epoch) return (null, new BlizzardAnswer(BlizzardStatus.NotAsked, Problem: "the client changed meanwhile"));
                _token = token;
                _tokenFor = client;
                // A minute early: a token that expires in flight is a 401 and a second token.
                _expires = now() + TimeSpan.FromSeconds(Math.Max(0, seconds - 60));
            }
            return (token, null);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return (null, new BlizzardAnswer(BlizzardStatus.Failed, Problem: "Battle.net's answer could not be read"));
        }
    }

    // Each URL is built here from the two hosts and an escaped name: nothing else can be asked.
    private async Task<(int Code, byte[]? Body, TimeSpan? RetryAfter, string? Problem)> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        request.Headers.UserAgent.ParseAdd("AltStableCompanion/" + ArmoryClient.SafeVersion(version));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(Timeout);
        using var http = new HttpClient(handler(), disposeHandler: true) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            var code = (int)response.StatusCode;
            var retry = ArmoryClient.RetryAfter(response, response.Headers.Date);
            if (code != (int)HttpStatusCode.OK) return (code, null, retry, null);
            if (response.Content.Headers.ContentLength is > MaxBytes) return (0, null, null, "the answer is too big");
            var body = await ArmoryClient.ReadCapped(response.Content, MaxBytes, deadline.Token);
            return body is null ? (0, null, null, "the answer is too big") : (code, body, null, null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return (0, null, null, $"no answer within {Timeout.TotalSeconds:0} seconds");
        }
        catch (HttpRequestException ex)
        {
            // The message names the host at most: never a header.
            return (0, null, null, ex.Message);
        }
    }
}
