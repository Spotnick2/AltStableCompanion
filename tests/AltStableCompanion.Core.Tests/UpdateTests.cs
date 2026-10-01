using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using AltStableCompanion.Core;

namespace AltStableCompanion.Core.Tests;

/// <summary>
/// GitHub, as the tests see it: every request the updater makes lands here, and nothing
/// reaches the network. One handler per client, since the client disposes its handler; the
/// log of requests is shared.
/// </summary>
internal sealed class FakeGitHub
{
    public List<HttpRequestMessage> Requests { get; } = [];
    public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } = _ => new HttpResponseMessage(HttpStatusCode.NotFound);

    public HttpMessageHandler Handler() => new Fake(this);

    public UpdateHooks Hooks(string version, string? exe) => new(version, exe, Handler);

    private sealed class Fake(FakeGitHub owner) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (owner.Requests) owner.Requests.Add(request);
            return Task.FromResult(owner.Respond(request));
        }
    }

    public static HttpResponseMessage Json(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    public static HttpResponseMessage Bytes(byte[] bytes) =>
        new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };

    public static HttpResponseMessage Text(string text) =>
        new(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8, "text/plain") };

    /// <summary>One release as GitHub lists it, with the exe and SHA256SUMS unless told otherwise.</summary>
    public static string Release(string tag, bool prerelease = false, bool draft = false, bool exe = true, bool sums = true, long size = 1234)
    {
        var version = tag.TrimStart('v');
        var assets = new List<string>();
        if (exe)
        {
            assets.Add($$"""{"name":"AltStableCompanion-{{version}}-win-x64.exe","size":{{size}},"browser_download_url":"https://github.com/Spotnick2/AltStableCompanion/releases/download/{{tag}}/AltStableCompanion-{{version}}-win-x64.exe"}""");
        }
        if (sums)
        {
            assets.Add($$"""{"name":"SHA256SUMS","size":110,"browser_download_url":"https://github.com/Spotnick2/AltStableCompanion/releases/download/{{tag}}/SHA256SUMS"}""");
        }
        return $$"""{"tag_name":"{{tag}}","name":"AltStable Companion {{tag}}","draft":{{(draft ? "true" : "false")}},"prerelease":{{(prerelease ? "true" : "false")}},"html_url":"https://github.com/Spotnick2/AltStableCompanion/releases/tag/{{tag}}","assets":[{{string.Join(",", assets)}}]}""";
    }

    public static string List(params string[] releases) => "[" + string.Join(",", releases) + "]";
}

public class ReleaseVersionTests
{
    [Theory]
    [InlineData("0.1.0-beta.1", "0.1.0-beta.2")]
    [InlineData("0.1.0-beta.2", "0.1.0-rc.1")]
    [InlineData("0.1.0-rc.1", "0.1.0")]
    [InlineData("0.1.0", "0.1.1")]
    [InlineData("0.1.1", "0.2.0")]
    [InlineData("0.9.9", "1.0.0")]
    [InlineData("0.1.0-beta.9", "0.1.0-beta.10")]
    [InlineData("0.1.0-alpha", "0.1.0-alpha.1")]
    public void Versions_are_ordered_as_semantic_versioning_orders_them(string lower, string higher)
    {
        var a = ReleaseVersion.TryParse(lower)!;
        var b = ReleaseVersion.TryParse(higher)!;
        Assert.True(a < b, $"{lower} < {higher}");
        Assert.True(b > a);
        Assert.False(b <= a);
    }

    [Fact]
    public void The_build_metadata_and_a_leading_v_do_not_count()
    {
        var stamped = ReleaseVersion.TryParse("0.1.0-beta.1+4eb2b19c0ffee")!;
        var tag = ReleaseVersion.TryParse("v0.1.0-beta.1")!;
        Assert.Equal(0, stamped.CompareTo(tag));
        Assert.True(stamped <= tag && stamped >= tag);
        Assert.Equal("0.1.0-beta.1", stamped.Text);
        Assert.Equal("4eb2b19c0ffee", stamped.Build);
        Assert.True(stamped.IsPreRelease);
        Assert.False(ReleaseVersion.TryParse("0.1.0+fcbace4")!.IsPreRelease);
    }

    [Theory]
    [InlineData("")]
    [InlineData("1.0")]
    [InlineData("1.0.0.0")]
    [InlineData("a.b.c")]
    [InlineData("1.0.0-")]
    [InlineData("-1.0.0")]
    [InlineData("0.0.0 ")]  // trimmed, so this one is fine: see below
    public void Anything_that_is_not_a_version_is_null(string text)
    {
        if (text == "0.0.0 ") { Assert.NotNull(ReleaseVersion.TryParse(text)); return; }
        Assert.Null(ReleaseVersion.TryParse(text));
    }
}

public class NewestReleaseTests
{
    private static readonly ReleaseVersion Beta = ReleaseVersion.TryParse("0.1.0-beta.1+4eb2b19")!;
    private static readonly ReleaseVersion Stable = ReleaseVersion.TryParse("0.1.0+fcbace4")!;

    [Fact]
    public void The_newest_release_that_is_newer_than_the_running_one_and_carries_both_files()
    {
        var json = FakeGitHub.List(
            FakeGitHub.Release("v0.3.0", draft: true),              // not out
            FakeGitHub.Release("v0.2.1", sums: false),              // nothing to check it against
            FakeGitHub.Release("v0.2.2", exe: false),               // nothing to download
            FakeGitHub.Release("v0.2.0", size: 50_000_000),
            FakeGitHub.Release("v0.1.5"),
            FakeGitHub.Release("v0.1.0"),
            FakeGitHub.Release("v0.1.0-beta.1", prerelease: true));
        var newest = Updater.Newest(Beta, json);
        Assert.NotNull(newest);
        Assert.Equal("0.2.0", newest.Version.Text);
        Assert.False(newest.PreRelease);
        Assert.Equal("AltStableCompanion-0.2.0-win-x64.exe", newest.ExeName);
        Assert.Equal(50_000_000, newest.ExeSize);
        Assert.EndsWith("/v0.2.0/AltStableCompanion-0.2.0-win-x64.exe", newest.ExeUrl);
        Assert.EndsWith("/v0.2.0/SHA256SUMS", newest.SumsUrl);
        Assert.Equal("https://github.com/Spotnick2/AltStableCompanion/releases/tag/v0.2.0", newest.Page);
    }

    [Fact]
    public void A_release_sees_only_releases_and_a_pre_release_sees_both()
    {
        var json = FakeGitHub.List(
            FakeGitHub.Release("v0.2.0-rc.1", prerelease: true),
            FakeGitHub.Release("v0.1.1"),
            FakeGitHub.Release("v0.1.0"));
        Assert.Equal("0.1.1", Updater.Newest(Stable, json)!.Version.Text);
        Assert.Equal("0.2.0-rc.1", Updater.Newest(Beta, json)!.Version.Text);
        // Running the newest there is: nothing.
        Assert.Null(Updater.Newest(ReleaseVersion.TryParse("0.1.1")!, json));
        Assert.Null(Updater.Newest(ReleaseVersion.TryParse("0.2.0-rc.1")!, json));
        // The list is not ordered by GitHub for us: the newest wins wherever it is.
        var reversed = FakeGitHub.List(FakeGitHub.Release("v0.1.0"), FakeGitHub.Release("v0.1.1"), FakeGitHub.Release("v0.1.2"));
        Assert.Equal("0.1.2", Updater.Newest(Stable, reversed)!.Version.Text);
    }

    [Fact]
    public void An_answer_that_is_not_a_list_is_an_error_not_an_update()
    {
        Assert.ThrowsAny<System.Text.Json.JsonException>(() => Updater.Newest(Stable, """{"message":"API rate limit exceeded"}"""));
        Assert.ThrowsAny<System.Text.Json.JsonException>(() => Updater.Newest(Stable, "<html>"));
        Assert.Null(Updater.Newest(Stable, "[]"));
        // A tag that is not a version is skipped, not fatal.
        Assert.Null(Updater.Newest(Stable, FakeGitHub.List(FakeGitHub.Release("nightly"))));
    }

    [Fact]
    public void The_sums_file_is_read_as_sha256sum_writes_it()
    {
        const string sums = "0123abcd  AltStableCompanion-0.2.0-win-x64.exe\nffff  *other.exe\n\n";
        Assert.Equal("0123abcd", Updater.HashFor(sums, "AltStableCompanion-0.2.0-win-x64.exe"));
        Assert.Equal("ffff", Updater.HashFor(sums, "other.exe"));
        Assert.Null(Updater.HashFor(sums, "AltStableCompanion-0.2.1-win-x64.exe"));
        Assert.Null(Updater.HashFor("", "x"));
    }
}

public class UpdaterTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(20);

    private static void Until(Func<bool> happened, string what)
    {
        var limit = DateTime.UtcNow + Limit;
        while (!happened())
        {
            Assert.True(DateTime.UtcNow < limit, "never happened: " + what);
            Thread.Sleep(25);
        }
    }

    private static string Hex(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    [Fact]
    public async Task A_check_asks_github_for_the_releases_with_the_headers_it_wants_and_finds_the_newer_one()
    {
        var gh = new FakeGitHub { Respond = _ => FakeGitHub.Json(FakeGitHub.List(FakeGitHub.Release("v0.2.0"), FakeGitHub.Release("v0.1.0"))) };
        var log = new List<string>();
        var u = new Updater(gh.Hooks("0.1.0+fcbace4", null), log.Add, () => new DateTime(2026, 10, 1, 12, 0, 0));
        var changes = 0;
        u.Changed += () => Interlocked.Increment(ref changes);
        Assert.Equal(UpdateStage.None, u.State.Stage);

        await u.CheckAsync();
        var s = u.State;
        Assert.Equal(UpdateStage.Available, s.Stage);
        Assert.Equal("0.2.0", s.Release!.Version.Text);
        Assert.Equal(new DateTime(2026, 10, 1, 12, 0, 0), s.CheckedAt);
        Assert.True(changes >= 2, "checking, then found");
        var request = Assert.Single(gh.Requests);
        Assert.Equal(Updater.ReleasesApi, request.RequestUri!.ToString());
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Contains(request.Headers.UserAgent, p => p.Product?.Name == "AltStableCompanion" && p.Product.Version == "0.1.0");
        Assert.Contains(request.Headers.Accept, a => a.MediaType == "application/vnd.github+json");
        Assert.Contains("update: 0.2.0 is out", log);

        // Up to date: the running version is the newest.
        var u2 = new Updater(gh.Hooks("0.2.0+abc", null), log.Add);
        await u2.CheckAsync();
        Assert.Equal(UpdateStage.UpToDate, u2.State.Stage);
        Assert.Null(u2.State.Release);
        Assert.NotNull(u2.State.CheckedAt);
    }

    [Fact]
    public async Task A_check_that_github_refuses_or_that_cannot_reach_it_is_a_failure_in_words()
    {
        var gh = new FakeGitHub { Respond = _ => new HttpResponseMessage(HttpStatusCode.Forbidden) { ReasonPhrase = "rate limit exceeded" } };
        var u = new Updater(gh.Hooks("0.1.0", null));
        await u.CheckAsync();
        Assert.Equal(UpdateStage.Failed, u.State.Stage);
        Assert.Equal("GitHub answered 403 rate limit exceeded", u.State.Problem);
        Assert.Null(u.State.Release);

        gh.Respond = _ => throw new HttpRequestException("No such host is known. (api.github.com:443)");
        await u.CheckAsync();
        Assert.Equal(UpdateStage.Failed, u.State.Stage);
        Assert.Equal("No such host is known. (api.github.com:443)", u.State.Problem);

        gh.Respond = _ => FakeGitHub.Json("not json");
        await u.CheckAsync();
        Assert.Equal(UpdateStage.Failed, u.State.Stage);
        Assert.NotNull(u.State.Problem);

        // A running version that is not one: nothing is compared, and the words say so.
        var odd = new Updater(gh.Hooks("0.0.0-dev+local", null));
        gh.Respond = _ => FakeGitHub.Json("[]");
        // "0.0.0-dev+local" IS a version; what is not is this:
        var none = new Updater(gh.Hooks("local build", null));
        await none.CheckAsync();
        Assert.Equal(UpdateStage.Failed, none.State.Stage);
        Assert.Contains("\"local build\"", none.State.Problem);
        await odd.CheckAsync();
        Assert.Equal(UpdateStage.UpToDate, odd.State.Stage);
    }

    [Fact]
    public async Task The_download_lands_beside_the_exe_checked_against_the_sums_and_a_wrong_hash_or_size_is_refused()
    {
        using var t = new TempInstall();
        var exe = Path.Combine(t.Root, "app", "AltStableCompanion.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(exe)!);
        File.WriteAllText(exe, "old");
        var bytes = new byte[300_000];
        new Random(7).NextBytes(bytes);
        var sums = $"{Hex(bytes)}  AltStableCompanion-0.2.0-win-x64.exe\n";
        var gh = new FakeGitHub();
        gh.Respond = r => r.RequestUri!.ToString() switch
        {
            Updater.ReleasesApi => FakeGitHub.Json(FakeGitHub.List(FakeGitHub.Release("v0.2.0", size: bytes.Length))),
            var url when url.EndsWith("/SHA256SUMS", StringComparison.Ordinal) => FakeGitHub.Text(sums),
            var url when url.EndsWith(".exe", StringComparison.Ordinal) => FakeGitHub.Bytes(bytes),
            var url => throw new InvalidOperationException("unexpected: " + url),
        };
        var log = new List<string>();
        var u = new Updater(gh.Hooks("0.1.0", exe), log.Add);
        var percents = new List<int>();
        u.Changed += () => { lock (percents) percents.Add(u.State.Percent); };

        // Nothing to download before a check found something.
        await u.DownloadAsync();
        Assert.Equal(UpdateStage.None, u.State.Stage);

        await u.CheckAsync();
        await u.DownloadAsync();
        var s = u.State;
        Assert.Equal(UpdateStage.Ready, s.Stage);
        Assert.Equal(exe + Updater.ReadySuffix, s.File);
        Assert.Equal(bytes, File.ReadAllBytes(s.File!));
        Assert.False(File.Exists(exe + Updater.PartSuffix));
        Assert.Equal("old", File.ReadAllText(exe));
        Assert.Equal(100, s.Percent);
        Assert.Contains(percents, p => p is > 0 and < 100);
        Assert.Contains(log, l => l.StartsWith("update: 0.2.0 downloaded and checked", StringComparison.Ordinal));
        // The sums were asked for before the exe.
        var urls = gh.Requests.Select(r => r.RequestUri!.ToString()).ToList();
        Assert.True(urls.IndexOf(urls.First(x => x.EndsWith("/SHA256SUMS", StringComparison.Ordinal)))
            < urls.IndexOf(urls.First(x => x.EndsWith(".exe", StringComparison.Ordinal))), "sums first");
        // Another check does not throw the ready file away, and another press downloads nothing.
        await u.CheckAsync();
        await u.DownloadAsync();
        Assert.Equal(UpdateStage.Ready, u.State.Stage);
        Assert.Equal(urls, gh.Requests.Select(r => r.RequestUri!.ToString()).ToList());

        // A wrong hash: refused, the part file gone, and nothing ready.
        File.Delete(s.File!);
        var wrong = new Updater(gh.Hooks("0.1.0", exe));
        sums = $"{Hex([1, 2, 3])}  AltStableCompanion-0.2.0-win-x64.exe\n";
        await wrong.CheckAsync();
        await wrong.DownloadAsync();
        Assert.Equal(UpdateStage.Failed, wrong.State.Stage);
        Assert.Equal("the downloaded file's SHA-256 is not the one SHA256SUMS lists", wrong.State.Problem);
        Assert.NotNull(wrong.State.Release);
        Assert.False(File.Exists(exe + Updater.PartSuffix));
        Assert.False(File.Exists(exe + Updater.ReadySuffix));

        // No line for the file in the sums: not even downloaded.
        sums = "abcd  something-else.exe\n";
        gh.Requests.Clear();
        var noLine = new Updater(gh.Hooks("0.1.0", exe));
        await noLine.CheckAsync();
        await noLine.DownloadAsync();
        Assert.Equal(UpdateStage.Failed, noLine.State.Stage);
        Assert.Contains("has no line for", noLine.State.Problem);
        Assert.DoesNotContain(gh.Requests, r => r.RequestUri!.ToString().EndsWith(".exe", StringComparison.Ordinal));

        // The size GitHub lists is not what came: refused, hash or no hash.
        sums = $"{Hex(bytes)}  AltStableCompanion-0.2.0-win-x64.exe\n";
        var shorter = bytes[..1000];
        var size = new Updater(gh.Hooks("0.1.0", exe));
        gh.Respond = r => r.RequestUri!.ToString() switch
        {
            Updater.ReleasesApi => FakeGitHub.Json(FakeGitHub.List(FakeGitHub.Release("v0.2.0", size: bytes.Length))),
            var url when url.EndsWith("/SHA256SUMS", StringComparison.Ordinal) => FakeGitHub.Text(sums),
            _ => FakeGitHub.Bytes(shorter),
        };
        await size.CheckAsync();
        await size.DownloadAsync();
        Assert.Equal(UpdateStage.Failed, size.State.Stage);
        Assert.Contains("GitHub lists", size.State.Problem);
        // And a failed download can be tried again from where it is.
        gh.Respond = r => r.RequestUri!.ToString().EndsWith(".exe", StringComparison.Ordinal) ? FakeGitHub.Bytes(bytes) : FakeGitHub.Text(sums);
        await size.DownloadAsync();
        Assert.Equal(UpdateStage.Ready, size.State.Stage);
    }

    [Fact]
    public async Task A_download_without_an_exe_path_or_too_large_for_any_release_is_refused_before_it_starts()
    {
        var gh = new FakeGitHub { Respond = _ => FakeGitHub.Json(FakeGitHub.List(FakeGitHub.Release("v0.2.0"))) };
        var u = new Updater(gh.Hooks("0.1.0", null));
        await u.CheckAsync();
        await u.DownloadAsync();
        Assert.Equal(UpdateStage.Failed, u.State.Stage);
        Assert.Equal("the running exe's path is not known", u.State.Problem);

        using var t = new TempInstall();
        var exe = Path.Combine(t.Root, "AltStableCompanion.exe");
        File.WriteAllText(exe, "old");
        gh.Respond = _ => FakeGitHub.Json(FakeGitHub.List(FakeGitHub.Release("v0.2.0", size: Updater.MaxExeBytes + 1)));
        var big = new Updater(gh.Hooks("0.1.0", exe));
        await big.CheckAsync();
        await big.DownloadAsync();
        Assert.Equal(UpdateStage.Failed, big.State.Stage);
        Assert.Contains("cannot be right", big.State.Problem);
        Assert.Equal(2, gh.Requests.Count);   // two checks, no download
    }

    [Fact]
    public void Install_puts_the_new_exe_where_the_old_one_was_and_undoes_itself_when_it_cannot()
    {
        using var t = new TempInstall();
        var exe = Path.Combine(t.Root, "AltStableCompanion.exe");
        var ready = exe + Updater.ReadySuffix;
        File.WriteAllText(exe, "old");
        File.WriteAllText(ready, "new");
        File.WriteAllText(exe + Updater.OldSuffix, "older still");
        Assert.Equal(exe, Updater.Install(exe, ready));
        Assert.Equal("new", File.ReadAllText(exe));
        Assert.Equal("old", File.ReadAllText(exe + Updater.OldSuffix));
        Assert.False(File.Exists(ready));

        // The second move fails (nothing to move): the first is undone.
        Assert.ThrowsAny<IOException>(() => Updater.Install(exe, ready));
        Assert.Equal("new", File.ReadAllText(exe));
        Assert.False(File.Exists(exe + Updater.OldSuffix));

        // The next start sweeps what an update leaves, and a download that was cut short.
        File.WriteAllText(exe + Updater.OldSuffix, "x");
        File.WriteAllText(exe + Updater.PartSuffix, "x");
        Updater.CleanUp(exe);
        Assert.False(File.Exists(exe + Updater.OldSuffix));
        Assert.False(File.Exists(exe + Updater.PartSuffix));
        Assert.True(File.Exists(exe));
        Updater.CleanUp(null);
    }

    [Fact]
    public async Task A_second_press_while_one_is_on_its_way_does_nothing_and_a_cancelled_download_leaves_no_part()
    {
        using var t = new TempInstall();
        var exe = Path.Combine(t.Root, "AltStableCompanion.exe");
        File.WriteAllText(exe, "old");
        var bytes = new byte[2_000_000];
        var gate = new ManualResetEventSlim(false);
        var gh = new FakeGitHub();
        gh.Respond = r =>
        {
            var url = r.RequestUri!.ToString();
            if (url == Updater.ReleasesApi) return FakeGitHub.Json(FakeGitHub.List(FakeGitHub.Release("v0.2.0", size: bytes.Length)));
            if (url.EndsWith("/SHA256SUMS", StringComparison.Ordinal)) return FakeGitHub.Text($"{Hex(bytes)}  AltStableCompanion-0.2.0-win-x64.exe\n");
            gate.Wait();
            return FakeGitHub.Bytes(bytes);
        };
        var u = new Updater(gh.Hooks("0.1.0", exe));
        await u.CheckAsync();
        var first = u.DownloadAsync();
        Until(() => gh.Requests.Count == 3, "the exe asked for");
        var second = u.DownloadAsync();
        Assert.Same(first, second);
        Assert.Same(first, u.CheckAsync());
        Assert.Equal(UpdateStage.Downloading, u.State.Stage);
        u.Cancel();
        gate.Set();
        await first;
        Assert.Equal(UpdateStage.Failed, u.State.Stage);
        Assert.Equal("the download was stopped", u.State.Problem);
        Assert.False(File.Exists(exe + Updater.PartSuffix));
        Assert.Equal(3, gh.Requests.Count);
    }
}

public class StalledDownloadTests
{
    /// <summary>A body whose first read never returns until the token says so: a connection that went quiet.</summary>
    private sealed class StalledStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public async Task A_body_that_stops_coming_after_the_headers_is_given_up_on_at_the_deadline()
    {
        using var t = new TempInstall();
        var exe = Path.Combine(t.Root, "AltStableCompanion.exe");
        File.WriteAllText(exe, "old");
        var gh = new FakeGitHub();
        gh.Respond = r =>
        {
            var url = r.RequestUri!.ToString();
            if (url == Updater.ReleasesApi) return FakeGitHub.Json(FakeGitHub.List(FakeGitHub.Release("v0.2.0", size: 5000)));
            if (url.EndsWith("/SHA256SUMS", StringComparison.Ordinal)) return FakeGitHub.Text("abcd  AltStableCompanion-0.2.0-win-x64.exe\n");
            // The headers at once, the body never.
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new StalledStream()) };
        };
        var u = new Updater(gh.Hooks("0.1.0", exe)) { DownloadTimeout = TimeSpan.FromMilliseconds(300) };
        await u.CheckAsync();
        var download = u.DownloadAsync();
        var finished = await Task.WhenAny(download, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.Same(download, finished);
        Assert.Equal(UpdateStage.Failed, u.State.Stage);
        Assert.Equal("the download did not finish within 0 minutes", u.State.Problem);
        Assert.False(File.Exists(exe + Updater.PartSuffix));
        // And the buttons are back: the next press starts another download.
        Assert.NotNull(u.State.Release);
    }
}

public class UpdateControllerTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(20);

    private static void Until(Func<bool> happened, string what)
    {
        var limit = DateTime.UtcNow + Limit;
        while (!happened())
        {
            Assert.True(DateTime.UtcNow < limit, "never happened: " + what);
            Thread.Sleep(25);
        }
    }

    private static Controller Started(TempInstall t, FakeGitHub gh, string? exe = null, Func<DateTime>? clock = null, string version = "0.1.0+fcbace4")
    {
        var c = new Controller(new StartupOptions(WowDir: t.Install.FlavorDir, DataDir: Path.Combine(t.Root, "appdata")),
            () => throw new InvalidOperationException("detection was used"), clock, update: gh.Hooks(version, exe));
        c.Start();
        return c;
    }

    [Fact]
    public void Nothing_is_asked_of_github_unless_the_player_presses_or_chose_the_window_check()
    {
        using var t = new TempInstall();
        var gh = new FakeGitHub { Respond = _ => FakeGitHub.Json(FakeGitHub.List(FakeGitHub.Release("v0.2.0"))) };
        var now = new DateTime(2026, 10, 1, 20, 0, 0);
        using var c = Started(t, gh, clock: () => now);
        Assert.Equal("0.1.0+fcbace4", c.Version);
        Assert.Null(c.Current.Update);
        Assert.False(c.Current.CheckUpdatesOnOpen);
        c.WindowOpened();
        c.WindowOpened();
        // Something that can only come after: the first pass, once watching has started.
        c.StartWatching();
        Until(() => c.Current.Shell.Report is not null, "the first pass");
        Assert.Empty(gh.Requests);

        // The press: one request, and the snapshot follows the updater.
        c.CheckForUpdates();
        Until(() => c.Current.Update?.Stage == UpdateStage.Available, "the release found");
        Assert.Single(gh.Requests);
        Assert.Equal("0.2.0", c.Current.Update!.Release!.Version.Text);
        Assert.Equal(now, c.Current.Update.CheckedAt);

        // The setting: written down, and the window opening checks - once an hour.
        c.SetCheckUpdatesOnOpen(true);
        Assert.True(c.Current.CheckUpdatesOnOpen);
        Assert.True(Settings.Load(Path.Combine(t.Root, "appdata")).CheckUpdatesOnOpen);
        c.WindowOpened();
        Until(() => gh.Requests.Count == 2, "the window's check");
        Until(() => c.Current.Update?.Stage == UpdateStage.Available, "found again");
        c.WindowOpened();
        now = now.AddMinutes(59);
        c.WindowOpened();
        Thread.Sleep(300);
        Assert.Equal(2, gh.Requests.Count);
        now = now.AddMinutes(2);
        c.WindowOpened();
        Until(() => gh.Requests.Count == 3, "an hour later");
        c.SetCheckUpdatesOnOpen(false);
        Assert.False(Settings.Load(Path.Combine(t.Root, "appdata")).CheckUpdatesOnOpen);
        now = now.AddHours(2);
        c.WindowOpened();
        Thread.Sleep(300);
        Assert.Equal(3, gh.Requests.Count);
    }

    [Fact]
    public void The_setting_survives_a_restart_and_the_check_runs_at_the_first_opening()
    {
        using var t = new TempInstall();
        var gh = new FakeGitHub { Respond = _ => FakeGitHub.Json("[]") };
        using (var c = Started(t, gh))
        {
            c.SetCheckUpdatesOnOpen(true);
        }
        using var again = Started(t, gh);
        Assert.True(again.Current.CheckUpdatesOnOpen);
        again.WindowOpened();
        Until(() => again.Current.Update?.Stage == UpdateStage.UpToDate, "checked");
        Assert.Single(gh.Requests);
    }

    [Fact]
    public async Task After_the_download_the_update_is_put_in_place_once_the_controller_has_stopped()
    {
        using var t = new TempInstall();
        var exe = Path.Combine(t.Root, "app", "AltStableCompanion.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(exe)!);
        File.WriteAllText(exe, "old");
        File.WriteAllText(exe + Updater.OldSuffix, "left by the last update");
        var bytes = new byte[5000];
        new Random(3).NextBytes(bytes);
        var gh = new FakeGitHub();
        gh.Respond = r => r.RequestUri!.ToString() switch
        {
            Updater.ReleasesApi => FakeGitHub.Json(FakeGitHub.List(FakeGitHub.Release("v0.2.0", size: bytes.Length))),
            var url when url.EndsWith("/SHA256SUMS", StringComparison.Ordinal) =>
                FakeGitHub.Text($"{Convert.ToHexStringLower(SHA256.HashData(bytes))}  AltStableCompanion-0.2.0-win-x64.exe\n"),
            _ => FakeGitHub.Bytes(bytes),
        };
        using var c = Started(t, gh, exe);
        // The start swept what the last update left.
        Assert.False(File.Exists(exe + Updater.OldSuffix));
        // Nothing ready: nothing moves.
        Assert.Equal("no update is ready", c.InstallUpdate().Problem);

        c.CheckForUpdates();
        Until(() => c.Current.Update?.Stage == UpdateStage.Available, "found");
        c.DownloadUpdate();
        Until(() => c.Current.Update?.Stage == UpdateStage.Ready, "downloaded");
        Assert.Equal("old", File.ReadAllText(exe));
        await c.StopAsync();
        var (start, problem) = c.InstallUpdate();
        Assert.Null(problem);
        Assert.Equal(exe, start);
        Assert.Equal(bytes, File.ReadAllBytes(exe));
        Assert.Equal("old", File.ReadAllText(exe + Updater.OldSuffix));
        Assert.Contains("update: 0.2.0 put in place of", File.ReadAllText(c.LogPath));
    }

    [Fact]
    public void Stopping_cancels_a_download_on_its_way()
    {
        using var t = new TempInstall();
        var exe = Path.Combine(t.Root, "AltStableCompanion.exe");
        File.WriteAllText(exe, "old");
        var bytes = new byte[100];
        var gate = new ManualResetEventSlim(false);
        var gh = new FakeGitHub();
        gh.Respond = r =>
        {
            var url = r.RequestUri!.ToString();
            if (url == Updater.ReleasesApi) return FakeGitHub.Json(FakeGitHub.List(FakeGitHub.Release("v0.2.0", size: bytes.Length)));
            if (url.EndsWith("/SHA256SUMS", StringComparison.Ordinal))
                return FakeGitHub.Text($"{Convert.ToHexStringLower(SHA256.HashData(bytes))}  AltStableCompanion-0.2.0-win-x64.exe\n");
            gate.Wait();
            return FakeGitHub.Bytes(bytes);
        };
        var c = Started(t, gh, exe);
        c.CheckForUpdates();
        Until(() => c.Current.Update?.Stage == UpdateStage.Available, "found");
        c.DownloadUpdate();
        Until(() => gh.Requests.Count == 3, "the exe asked for");
        c.Dispose();
        gate.Set();
        Until(() => c.Current.Update?.Stage == UpdateStage.Failed, "stopped");
        Assert.Equal("the download was stopped", c.Current.Update!.Problem);
        Assert.Equal("old", File.ReadAllText(exe));
    }
}

public class UpdateWordsTests
{
    private static readonly DateTime Now = new(2026, 10, 1, 21, 0, 0);

    [Fact]
    public void The_version_line_names_the_version_and_the_build()
    {
        Assert.Equal("Version 0.1.0-beta.1 (build 4eb2b19)", PassText.VersionLine("0.1.0-beta.1+4eb2b19a8c4ef0d2f1e2b3c4d5e6f7a8b9c0d1e2"));
        Assert.Equal("Version 0.1.0 (build fcbace4, with uncommitted changes)", PassText.VersionLine("0.1.0+fcbace4000000-dirty"));
        Assert.Equal("Version 0.2.0", PassText.VersionLine("0.2.0"));
        Assert.Equal("Version local build", PassText.VersionLine("local build"));
    }

    [Fact]
    public void The_update_line_says_where_things_stand_in_the_players_terms()
    {
        var release = Updater.Newest(ReleaseVersion.TryParse("0.1.0")!, FakeGitHub.List(FakeGitHub.Release("v0.2.0")))!;
        var pre = Updater.Newest(ReleaseVersion.TryParse("0.1.0-beta.1")!, FakeGitHub.List(FakeGitHub.Release("v0.2.0-rc.1", prerelease: true)))!;
        Assert.Null(PassText.UpdateLine(null, Now));
        Assert.Null(PassText.UpdateLine(new UpdateState(), Now));
        Assert.Equal("Checking GitHub…", PassText.UpdateLine(new UpdateState(UpdateStage.Checking), Now));
        Assert.Equal("This is the newest release (checked today 20:58:00).", PassText.UpdateLine(new UpdateState(UpdateStage.UpToDate, CheckedAt: Now.AddMinutes(-2)), Now));
        Assert.Equal("0.2.0 is out. Update now downloads it; nothing changes until you restart.",
            PassText.UpdateLine(new UpdateState(UpdateStage.Available, release), Now));
        Assert.Equal("0.2.0-rc.1 is out (a pre-release). Update now downloads it; nothing changes until you restart.",
            PassText.UpdateLine(new UpdateState(UpdateStage.Available, pre), Now));
        Assert.Equal("Downloading 0.2.0 (37%)…", PassText.UpdateLine(new UpdateState(UpdateStage.Downloading, release, 37), Now));
        Assert.Equal("0.2.0 is downloaded and checked. Restart now puts it in place; the app comes back on its own.",
            PassText.UpdateLine(new UpdateState(UpdateStage.Ready, release, 100, File: "x"), Now));
        Assert.Equal("Could not check for updates: GitHub answered 403 rate limit exceeded.",
            PassText.UpdateLine(new UpdateState(UpdateStage.Failed, Problem: "GitHub answered 403 rate limit exceeded"), Now));
        Assert.Equal("Could not download 0.2.0: the download was stopped. You can also download it from the release page.",
            PassText.UpdateLine(new UpdateState(UpdateStage.Failed, release, Problem: "the download was stopped"), Now));
    }

    [Fact]
    public void The_about_links_are_https_and_point_at_the_repositories()
    {
        Assert.All(PassText.AboutLinks, l => Assert.StartsWith("https://github.com/", l.Url));
        Assert.Contains(PassText.AboutLinks, l => l.Label == "Report an issue" && l.Url.EndsWith("/AltStableCompanion/issues", StringComparison.Ordinal));
        Assert.Contains(PassText.AboutLinks, l => l.Url.EndsWith("/Spotnick2/AltStable", StringComparison.Ordinal));
        Assert.Contains(PassText.AboutLinks, l => l.Url.EndsWith("/LICENSE", StringComparison.Ordinal));
        Assert.Contains(PassText.AboutLinks, l => l.Url.EndsWith("/Reference/Blizzard/README.md", StringComparison.Ordinal));
    }
}
