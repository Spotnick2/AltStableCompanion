using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace AltStableCompanion.Core;

/// <summary>
/// A version as a tag names it: <c>0.2.0</c>, <c>0.2.0-rc.1</c>, <c>0.1.0-beta.1+4eb2b19</c>.
/// Ordered as semantic versioning orders them: a pre-release comes before the release it
/// precedes, and the build metadata after <c>+</c> does not count.
/// </summary>
public sealed record ReleaseVersion(int Major, int Minor, int Patch, string PreRelease, string Build) : IComparable<ReleaseVersion>
{
    public bool IsPreRelease => PreRelease.Length > 0;

    /// <summary>What the player reads: no build metadata.</summary>
    public string Text => $"{Major}.{Minor}.{Patch}" + (IsPreRelease ? "-" + PreRelease : "");

    /// <summary>With a leading <c>v</c> or not, with or without build metadata; null for anything else.</summary>
    public static ReleaseVersion? TryParse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var s = text.Trim();
        if (s.StartsWith('v') || s.StartsWith('V')) s = s[1..];
        var build = "";
        var plus = s.IndexOf('+');
        if (plus >= 0) { build = s[(plus + 1)..]; s = s[..plus]; }
        var pre = "";
        var dash = s.IndexOf('-');
        if (dash >= 0) { pre = s[(dash + 1)..]; s = s[..dash]; }
        var parts = s.Split('.');
        if (parts.Length != 3) return null;
        var n = new int[3];
        for (var i = 0; i < 3; i++)
        {
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out n[i])) return null;
        }
        if (dash >= 0 && pre.Length == 0) return null;
        return new ReleaseVersion(n[0], n[1], n[2], pre, build);
    }

    public int CompareTo(ReleaseVersion? other)
    {
        if (other is null) return 1;
        var c = Major.CompareTo(other.Major);
        if (c == 0) c = Minor.CompareTo(other.Minor);
        if (c == 0) c = Patch.CompareTo(other.Patch);
        if (c != 0) return c;
        if (IsPreRelease != other.IsPreRelease) return IsPreRelease ? -1 : 1;
        if (!IsPreRelease) return 0;
        // Dot-separated identifiers: numbers against numbers, otherwise text; the shorter list first.
        var a = PreRelease.Split('.');
        var b = other.PreRelease.Split('.');
        for (var i = 0; i < Math.Min(a.Length, b.Length); i++)
        {
            var an = int.TryParse(a[i], NumberStyles.None, CultureInfo.InvariantCulture, out var ai);
            var bn = int.TryParse(b[i], NumberStyles.None, CultureInfo.InvariantCulture, out var bi);
            c = an && bn ? ai.CompareTo(bi) : an ? -1 : bn ? 1 : string.CompareOrdinal(a[i], b[i]);
            if (c != 0) return c;
        }
        return a.Length.CompareTo(b.Length);
    }

    public static bool operator <(ReleaseVersion a, ReleaseVersion b) => a.CompareTo(b) < 0;
    public static bool operator >(ReleaseVersion a, ReleaseVersion b) => a.CompareTo(b) > 0;
    public static bool operator <=(ReleaseVersion a, ReleaseVersion b) => a.CompareTo(b) <= 0;
    public static bool operator >=(ReleaseVersion a, ReleaseVersion b) => a.CompareTo(b) >= 0;
}

/// <summary>One GitHub Release that carries this app's exe.</summary>
/// <param name="Page">The release page, for the browser.</param>
/// <param name="ExeName">The asset, <c>AltStableCompanion-&lt;version&gt;-win-x64.exe</c>.</param>
/// <param name="ExeUrl">Where the asset is downloaded from.</param>
/// <param name="ExeSize">Its size in bytes, as GitHub lists it.</param>
/// <param name="SumsUrl">The <c>SHA256SUMS</c> beside it.</param>
public sealed record Release(ReleaseVersion Version, bool PreRelease, string Page, string ExeName, string ExeUrl, long ExeSize, string SumsUrl);

public enum UpdateStage
{
    /// <summary>Nothing has been looked for since the app started.</summary>
    None,
    Checking,
    /// <summary>Looked, and this is the newest.</summary>
    UpToDate,
    /// <summary>A newer release exists; nothing has been downloaded.</summary>
    Available,
    Downloading,
    /// <summary>Downloaded and checked against SHA256SUMS; a restart puts it in place.</summary>
    Ready,
    /// <summary>The check or the download did not work; <see cref="UpdateState.Problem"/> says why.</summary>
    Failed,
}

/// <summary>Where the update stands. Replaced whole.</summary>
/// <param name="Release">The newer release, from Available on.</param>
/// <param name="Percent">Of the download, while downloading.</param>
/// <param name="Problem">Why it failed, in the player's terms.</param>
/// <param name="CheckedAt">When the last check that got an answer ended.</param>
/// <param name="File">The downloaded exe, once Ready.</param>
public sealed record UpdateState(UpdateStage Stage = UpdateStage.None, Release? Release = null, int Percent = 0,
    string? Problem = null, DateTime? CheckedAt = null, string? File = null);

/// <summary>
/// How the updater reaches GitHub and the file system: the running version, the exe that
/// would be replaced, and the HTTP handler - a fake in the tests, which never reach the
/// network. The version and the exe come from the entry assembly in the app.
/// </summary>
public sealed record UpdateHooks(string Version, string? ExePath, Func<HttpMessageHandler> Http)
{
    public static UpdateHooks Real() =>
        new(AppVersion.Informational, Environment.ProcessPath, () => new SocketsHttpHandler { AllowAutoRedirect = true });
}

/// <summary>The version the running app carries: what the build stamped, read once.</summary>
public static class AppVersion
{
    /// <summary>
    /// The informational version, <c>0.1.0-beta.1+4eb2b19…</c>: the props' version, and the
    /// commit the build was made from (<c>-dirty</c> when the tree had uncommitted changes).
    /// </summary>
    public static string Informational =>
        System.Reflection.Assembly.GetEntryAssembly()?
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion
        ?? "0.0.0";
}

/// <summary>
/// Looks for a newer release on GitHub, downloads it, checks it, and puts it in place of the
/// running exe. Nothing here happens on its own: the controller calls it when the player
/// presses a button, or when the player chose to have the window check. The running version
/// decides what counts: a pre-release sees pre-releases, a release sees only releases.
/// </summary>
public sealed class Updater(UpdateHooks hooks, Action<string>? log = null, Func<DateTime>? clock = null)
{
    public const string Repository = "Spotnick2/AltStableCompanion";
    public const string ReleasesApi = "https://api.github.com/repos/" + Repository + "/releases?per_page=20";
    public static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(10);
    /// <summary>A downloaded exe larger than this is not ours.</summary>
    public const long MaxExeBytes = 200L * 1024 * 1024;
    /// <summary>The suffixes beside the running exe: the download in progress, the one checked, the one replaced.</summary>
    public const string PartSuffix = ".update.part";
    public const string ReadySuffix = ".update";
    public const string OldSuffix = ".old";

    private readonly Func<DateTime> _clock = clock ?? (() => DateTime.Now);
    private readonly Lock _gate = new();
    private UpdateState _state = new();
    private CancellationTokenSource? _download;
    private Task? _busy;

    /// <summary>The state changed. On whatever thread changed it.</summary>
    public event Action? Changed;

    public UpdateState State
    {
        get { lock (_gate) return _state; }
    }

    /// <summary>The version this app runs, as the player reads it, or the raw stamp when it does not parse.</summary>
    public ReleaseVersion? Running { get; } = ReleaseVersion.TryParse(hooks.Version);

    /// <summary>
    /// Ask GitHub. One check or download at a time: a second press while one is on its way
    /// does nothing. Returns when the check is over.
    /// </summary>
    public Task CheckAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_busy is { IsCompleted: false }) return _busy;
            // The exe already downloaded and checked stays ready: a check does not throw it away.
            if (_state.Stage == UpdateStage.Ready) return Task.CompletedTask;
            _state = _state with { Stage = UpdateStage.Checking, Problem = null };
            _busy = Task.Run(() => CheckCoreAsync(ct), CancellationToken.None);
            return _busy;
        }
    }

    private async Task CheckCoreAsync(CancellationToken ct)
    {
        Changed?.Invoke();
        UpdateState next;
        try
        {
            var newest = await NewestAsync(ct);
            var at = _clock();
            next = newest is null
                ? new UpdateState(UpdateStage.UpToDate, CheckedAt: at)
                : new UpdateState(UpdateStage.Available, newest, CheckedAt: at);
            log?.Invoke(newest is null ? $"update: up to date ({hooks.Version})" : $"update: {newest.Version.Text} is out");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or IOException or InvalidOperationException)
        {
            next = new UpdateState(UpdateStage.Failed, Problem: ex is TaskCanceledException ? "GitHub did not answer in time" : ex.Message,
                CheckedAt: State.CheckedAt);
            log?.Invoke($"update: the check failed: {ex.Message}");
        }
        lock (_gate) _state = next;
        Changed?.Invoke();
    }

    /// <summary>The newest release this app may move to, or null when the running one is it.</summary>
    internal async Task<Release?> NewestAsync(CancellationToken ct)
    {
        if (Running is null) throw new InvalidOperationException($"the running version, \"{hooks.Version}\", is not a release version");
        using var http = Client(CheckTimeout);
        using var request = new HttpRequestMessage(HttpMethod.Get, ReleasesApi);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"GitHub answered {(int)response.StatusCode} {response.ReasonPhrase}".TrimEnd());
        }
        var json = await response.Content.ReadAsStringAsync(ct);
        return Newest(Running, json);
    }

    /// <summary>
    /// The newest release in GitHub's list that is newer than the running version and carries
    /// the exe and its sums; pre-releases only when the running version is one. Drafts never.
    /// </summary>
    public static Release? Newest(ReleaseVersion running, string releasesJson)
    {
        using var doc = JsonDocument.Parse(releasesJson);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) throw new JsonException("GitHub's answer is not a list of releases");
        Release? best = null;
        foreach (var r in doc.RootElement.EnumerateArray())
        {
            if (r.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True) continue;
            var pre = r.TryGetProperty("prerelease", out var p) && p.ValueKind == JsonValueKind.True;
            if (pre && !running.IsPreRelease) continue;
            var version = ReleaseVersion.TryParse(r.TryGetProperty("tag_name", out var tag) ? tag.GetString() : null);
            if (version is null || version <= running) continue;
            if (best is not null && version <= best.Version) continue;
            var page = r.TryGetProperty("html_url", out var url) ? url.GetString() : null;
            if (string.IsNullOrEmpty(page) || !r.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) continue;
            string? exeName = null, exeUrl = null, sumsUrl = null;
            long size = 0;
            var wanted = $"AltStableCompanion-{version.Text}-win-x64.exe";
            foreach (var a in assets.EnumerateArray())
            {
                var name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
                var link = a.TryGetProperty("browser_download_url", out var l) ? l.GetString() : null;
                if (name is null || string.IsNullOrEmpty(link)) continue;
                if (name == wanted) { exeName = name; exeUrl = link; size = a.TryGetProperty("size", out var s) && s.TryGetInt64(out var v) ? v : 0; }
                else if (name == "SHA256SUMS") sumsUrl = link;
            }
            // A release without both files is not one this app can install: a rehearsal, or
            // one whose upload failed. Not offered.
            if (exeName is null || exeUrl is null || sumsUrl is null) continue;
            best = new Release(version, pre, page, exeName, exeUrl, size, sumsUrl);
        }
        return best;
    }

    /// <summary>
    /// Download the release found, beside the running exe, and check it against SHA256SUMS.
    /// Only from Available (or a failed download). Returns when it is over.
    /// </summary>
    public Task DownloadAsync()
    {
        Release release;
        CancellationTokenSource cts;
        lock (_gate)
        {
            if (_busy is { IsCompleted: false }) return _busy;
            if (_state.Release is not { } r || _state.Stage is not (UpdateStage.Available or UpdateStage.Failed)) return Task.CompletedTask;
            release = r;
            cts = _download = new CancellationTokenSource();
            _state = new UpdateState(UpdateStage.Downloading, release, 0, CheckedAt: _state.CheckedAt);
            _busy = Task.Run(() => DownloadCoreAsync(release, cts.Token), CancellationToken.None);
            return _busy;
        }
    }

    /// <summary>Stop a download on its way; the part file goes. Nothing else is undone.</summary>
    public void Cancel()
    {
        CancellationTokenSource? cts;
        lock (_gate) cts = _download;
        cts?.Cancel();
    }

    private async Task DownloadCoreAsync(Release release, CancellationToken ct)
    {
        Changed?.Invoke();
        UpdateState next;
        var exe = hooks.ExePath;
        var part = exe + PartSuffix;
        try
        {
            if (string.IsNullOrEmpty(exe)) throw new InvalidOperationException("the running exe's path is not known");
            if (release.ExeSize > MaxExeBytes) throw new InvalidOperationException($"the file on GitHub is {release.ExeSize / (1024 * 1024)} MB, which cannot be right");
            using var http = Client(DownloadTimeout);
            // The sums first, and small: a release whose sums cannot be had is not downloaded.
            var sums = await http.GetStringAsync(release.SumsUrl, ct);
            var expected = HashFor(sums, release.ExeName)
                ?? throw new InvalidOperationException($"SHA256SUMS on GitHub has no line for {release.ExeName}");
            await DownloadToAsync(http, release, part, ct);
            var actual = await HashOfAsync(part, ct);
            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("the downloaded file's SHA-256 is not the one SHA256SUMS lists");
            }
            var ready = exe + ReadySuffix;
            File.Move(part, ready, overwrite: true);
            next = new UpdateState(UpdateStage.Ready, release, 100, CheckedAt: State.CheckedAt, File: ready);
            log?.Invoke($"update: {release.Version.Text} downloaded and checked: {ready}");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException
            or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            TryDelete(part);
            var why = ex is OperationCanceledException && ct.IsCancellationRequested ? "the download was stopped"
                : ex is TaskCanceledException ? "GitHub did not answer in time" : ex.Message;
            next = new UpdateState(UpdateStage.Failed, release, Problem: why, CheckedAt: State.CheckedAt);
            log?.Invoke($"update: the download failed: {why}");
        }
        lock (_gate)
        {
            _state = next;
            _download = null;
        }
        Changed?.Invoke();
    }

    private async Task DownloadToAsync(HttpClient http, Release release, string part, CancellationToken ct)
    {
        using var response = await http.GetAsync(release.ExeUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"GitHub answered {(int)response.StatusCode} {response.ReasonPhrase}".TrimEnd());
        }
        var total = response.Content.Headers.ContentLength ?? release.ExeSize;
        await using var input = await response.Content.ReadAsStreamAsync(ct);
        await using var output = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
        var buffer = new byte[1 << 16];
        long done = 0;
        var shown = -1;
        int read;
        while ((read = await input.ReadAsync(buffer, ct)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, read), ct);
            done += read;
            if (done > MaxExeBytes) throw new InvalidOperationException("the download is larger than any release of this app");
            var percent = total > 0 ? (int)Math.Min(99, done * 100 / total) : 0;
            if (percent != shown)
            {
                shown = percent;
                lock (_gate) _state = _state with { Percent = percent };
                Changed?.Invoke();
            }
        }
        if (release.ExeSize > 0 && done != release.ExeSize)
        {
            throw new InvalidOperationException($"the download is {done} bytes; GitHub lists {release.ExeSize}");
        }
    }

    /// <summary>The hash SHA256SUMS gives for the file, or null: <c>&lt;hex&gt;  &lt;name&gt;</c>, one per line.</summary>
    public static string? HashFor(string sums, string name)
    {
        foreach (var raw in sums.Split('\n'))
        {
            var line = raw.Trim();
            var gap = line.IndexOf(' ');
            if (gap <= 0) continue;
            var file = line[gap..].TrimStart(' ', '*');
            if (file == name) return line[..gap];
        }
        return null;
    }

    private static async Task<string> HashOfAsync(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct));
    }

    /// <summary>
    /// Put the downloaded exe where the running one is: the running one is renamed (a running
    /// file can be renamed, not overwritten) and the new one moved in. When the second move
    /// fails the first is undone, so the player is left with what they had. The path of the
    /// exe to start, which is the path that was running.
    /// </summary>
    public static string Install(string exe, string ready)
    {
        var old = exe + OldSuffix;
        TryDelete(old);
        File.Move(exe, old);
        try
        {
            File.Move(ready, exe);
        }
        catch
        {
            File.Move(old, exe);
            throw;
        }
        return exe;
    }

    /// <summary>At start: what the last update left behind, and a download that was cut short.</summary>
    public static void CleanUp(string? exe)
    {
        if (string.IsNullOrEmpty(exe)) return;
        TryDelete(exe + OldSuffix);
        TryDelete(exe + PartSuffix);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The old exe may still be closing; the next start tries again.
        }
    }

    private HttpClient Client(TimeSpan timeout)
    {
        var http = new HttpClient(hooks.Http(), disposeHandler: true) { Timeout = timeout };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("AltStableCompanion/" + (Running?.Text ?? "0"));
        return http;
    }
}
