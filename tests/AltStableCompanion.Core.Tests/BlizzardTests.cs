using System.Net;
using System.Text;
using AltStableCompanion.Core;
using Xunit;

namespace AltStableCompanion.Core.Tests;

/// <summary>A Battle.net that answers by host: a token, a profile, and the render CDN's 403.</summary>
internal static class FakeBattleNet
{
    public const string Token = "tok-0123456789";
    public const string Secret = "s3cret-0123456789abcdef";
    public static readonly BlizzardClient Client = new("client-id-42", Secret);

    public static HttpResponseMessage Json(string json, HttpStatusCode code = HttpStatusCode.OK) =>
        new(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    public static HttpResponseMessage TokenOk() => Json($"{{\"access_token\":\"{Token}\",\"token_type\":\"bearer\",\"expires_in\":86399}}");

    public static HttpResponseMessage Profile(long id, int level, DateTimeOffset? login = null) =>
        Json($"{{\"id\":{id},\"name\":\"Karuzo\",\"level\":{level}"
            + (login is { } l ? $",\"last_login_timestamp\":{l.ToUnixTimeMilliseconds()}" : "") + "}");

    public static bool IsToken(HttpRequestMessage r) => r.RequestUri!.Host == BlizzardApi.TokenHost;
    public static bool IsApi(HttpRequestMessage r) => r.RequestUri!.Host == BlizzardApi.ApiHost;

    public static int Count(FakeCdn cdn, string host)
    {
        lock (cdn.Requests) return cdn.Requests.Count(q => new Uri(q.Url).Host == host);
    }
}

public class BlizzardApiTests
{
    private const long Karuzo = 678282;          // Player-6796-000A598A
    private static readonly DateTimeOffset Login = new(2026, 11, 11, 4, 11, 0, TimeSpan.Zero);

    private static BlizzardApi Api(FakeCdn cdn) => new(() => cdn, "1.2.0", () => DateTimeOffset.UtcNow);
    private static bool Always() => true;

    [Fact]
    public async Task Nothing_naming_the_character_is_sent_once_it_is_no_longer_allowed_after_a_token_or_a_401()
    {
        var cdn = new FakeCdn
        {
            Respond = r => FakeBattleNet.IsToken(r) ? FakeBattleNet.TokenOk() : new HttpResponseMessage(HttpStatusCode.Unauthorized),
        };
        var api = Api(cdn);
        var asked = 0;
        // Allowed for the first request only: the 401's new token is fetched, the request is not sent again.
        var answer = await api.ProfileAsync(FakeBattleNet.Client, "Karuzo Macphisto", Karuzo, Always, () => ++asked == 1, default);
        Assert.Equal(BlizzardStatus.NotAsked, answer.Status);
        Assert.Equal(1, FakeBattleNet.Count(cdn, BlizzardApi.ApiHost));
        Assert.Equal(2, FakeBattleNet.Count(cdn, BlizzardApi.TokenHost));
        // Not allowed at all: the token, and nothing else.
        Assert.Equal(BlizzardStatus.NotAsked, (await api.ProfileAsync(FakeBattleNet.Client, "Karuzo Macphisto", Karuzo, Always, () => false, default)).Status);
        Assert.Equal(1, FakeBattleNet.Count(cdn, BlizzardApi.ApiHost));
    }

    [Fact]
    public async Task A_401_for_a_client_no_longer_current_asks_for_no_new_token()
    {
        var retired = false;
        var cdn = new FakeCdn
        {
            Respond = r =>
            {
                if (FakeBattleNet.IsToken(r)) return FakeBattleNet.TokenOk();
                retired = true;                                       // removed while its request was out
                return new HttpResponseMessage(HttpStatusCode.Unauthorized);
            },
        };
        var answer = await Api(cdn).ProfileAsync(FakeBattleNet.Client, "Karuzo Macphisto", Karuzo, () => !retired, Always, default);
        Assert.Equal(BlizzardStatus.NotAsked, answer.Status);
        Assert.Equal(1, FakeBattleNet.Count(cdn, BlizzardApi.TokenHost));
        Assert.Equal(1, FakeBattleNet.Count(cdn, BlizzardApi.ApiHost));
    }

    [Fact]
    public async Task A_token_asked_for_while_the_client_is_forgotten_is_not_kept()
    {
        BlizzardApi? api = null;
        var tokens = 0;
        var cdn = new FakeCdn
        {
            Respond = r =>
            {
                if (!FakeBattleNet.IsToken(r)) return FakeBattleNet.Profile(Karuzo, 23);
                if (++tokens == 1) api!.Forget();                      // removed while the first token was on its way
                return FakeBattleNet.TokenOk();
            },
        };
        api = Api(cdn);
        Assert.Equal(BlizzardStatus.NotAsked, (await api.ProfileAsync(FakeBattleNet.Client, "Karuzo Macphisto", Karuzo, Always, Always, default)).Status);
        Assert.Equal(0, FakeBattleNet.Count(cdn, BlizzardApi.ApiHost));
        Assert.Equal(BlizzardStatus.Ok, (await api.ProfileAsync(FakeBattleNet.Client, "Karuzo Macphisto", Karuzo, Always, Always, default)).Status);
        Assert.Equal(2, tokens);                                      // a new one: the first was not kept
    }

    [Fact]
    public async Task A_token_then_the_profile_each_to_its_own_host_with_its_own_credential()
    {
        var cdn = new FakeCdn
        {
            Respond = r => FakeBattleNet.IsToken(r) ? FakeBattleNet.TokenOk() : FakeBattleNet.Profile(Karuzo, 23, Login),
        };
        var answer = await Api(cdn).ProfileAsync(FakeBattleNet.Client, "Karuzo Macphisto", Karuzo, Always, Always, default);

        Assert.Equal(BlizzardStatus.Ok, answer.Status);
        Assert.Equal(new BlizzardProfile(23, Login), answer.Profile);
        Assert.Equal("https://oauth.battle.net/token", cdn.Requests[0].Url);
        Assert.Equal("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("client-id-42:" + FakeBattleNet.Secret)), cdn.Auth[0]);
        Assert.Equal("https://us.api.blizzard.com/profile/wow/character/karuzo-macphisto?namespace=profile-forever-us&locale=en_US", cdn.Requests[1].Url);
        Assert.Equal("Bearer " + FakeBattleNet.Token, cdn.Auth[1]);
        Assert.StartsWith("AltStableCompanion/1.2.0", cdn.Requests[1].Agent);
    }

    [Fact]
    public async Task The_token_is_kept_until_it_expires_and_a_401_gets_one_new_one()
    {
        var profileCodes = new Queue<HttpStatusCode>([HttpStatusCode.OK, HttpStatusCode.Unauthorized, HttpStatusCode.OK,
            HttpStatusCode.Unauthorized, HttpStatusCode.Unauthorized]);
        var cdn = new FakeCdn
        {
            Respond = r => FakeBattleNet.IsToken(r) ? FakeBattleNet.TokenOk()
                : profileCodes.Dequeue() is var code && code == HttpStatusCode.OK ? FakeBattleNet.Profile(Karuzo, 23)
                : new HttpResponseMessage(code),
        };
        var api = Api(cdn);
        Assert.Equal(BlizzardStatus.Ok, (await api.ProfileAsync(FakeBattleNet.Client, "Karuzo Macphisto", Karuzo, Always, Always, default)).Status);
        Assert.Equal(1, FakeBattleNet.Count(cdn, BlizzardApi.TokenHost));
        // A 401: one new token, and the same question again.
        Assert.Equal(BlizzardStatus.Ok, (await api.ProfileAsync(FakeBattleNet.Client, "Karuzo Macphisto", Karuzo, Always, Always, default)).Status);
        Assert.Equal(2, FakeBattleNet.Count(cdn, BlizzardApi.TokenHost));
        // A 401 with the new token too: refused, and no third token.
        Assert.Equal(BlizzardStatus.Refused, (await api.ProfileAsync(FakeBattleNet.Client, "Karuzo Macphisto", Karuzo, Always, Always, default)).Status);
        Assert.Equal(3, FakeBattleNet.Count(cdn, BlizzardApi.TokenHost));
        Assert.Equal(8, cdn.Count);                                   // 2, then 3 (401, token, 200), then 3 (401, token, 401)
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, BlizzardStatus.Refused)]
    [InlineData(HttpStatusCode.BadRequest, BlizzardStatus.Refused)]
    [InlineData(HttpStatusCode.ServiceUnavailable, BlizzardStatus.Busy)]
    [InlineData(HttpStatusCode.TooManyRequests, BlizzardStatus.Busy)]
    [InlineData(HttpStatusCode.Redirect, BlizzardStatus.Failed)]
    public async Task A_token_that_is_not_given_says_why_and_nothing_else_is_asked(HttpStatusCode code, BlizzardStatus status)
    {
        var cdn = new FakeCdn { Respond = _ => new HttpResponseMessage(code) };
        var answer = await Api(cdn).ProfileAsync(FakeBattleNet.Client, "Karuzo Macphisto", Karuzo, Always, Always, default);
        Assert.Equal(status, answer.Status);
        Assert.Equal(1, cdn.Count);
        Assert.DoesNotContain(FakeBattleNet.Secret, answer.Problem ?? "");
    }

    [Theory]
    [InlineData(678283, BlizzardStatus.NotFound)]   // a namesake, or a rename: someone else's profile
    [InlineData(Karuzo, BlizzardStatus.Ok)]
    public async Task A_profile_counts_only_when_its_id_is_the_guid_s_number(long id, BlizzardStatus status)
    {
        var cdn = new FakeCdn { Respond = r => FakeBattleNet.IsToken(r) ? FakeBattleNet.TokenOk() : FakeBattleNet.Profile(id, 30) };
        var answer = await Api(cdn).ProfileAsync(FakeBattleNet.Client, "Karuzo Macphisto", Karuzo, Always, Always, default);
        Assert.Equal(status, answer.Status);
        Assert.Equal(status == BlizzardStatus.Ok ? 30 : null, answer.Profile?.Level);
    }

    [Fact]
    public async Task No_profile_and_a_busy_api_are_told_apart()
    {
        var code = HttpStatusCode.NotFound;
        var cdn = new FakeCdn { Respond = r => FakeBattleNet.IsToken(r) ? FakeBattleNet.TokenOk() : new HttpResponseMessage(code) };
        var api = Api(cdn);
        Assert.Equal(BlizzardStatus.NotFound, (await api.ProfileAsync(FakeBattleNet.Client, "Karuzo Macphisto", Karuzo, Always, Always, default)).Status);
        code = HttpStatusCode.TooManyRequests;
        Assert.Equal(BlizzardStatus.Busy, (await api.ProfileAsync(FakeBattleNet.Client, "Karuzo Macphisto", Karuzo, Always, Always, default)).Status);
    }

    [Theory]
    [InlineData("Karuzo Macphisto", "karuzo-macphisto")]
    [InlineData("Zoruka", "zoruka")]
    [InlineData("Ålfrida Sumner", "%C3%A5lfrida-sumner")]
    [InlineData("a/b?c", "a%2Fb%3Fc")]
    public void The_name_in_the_path_is_one_lower_case_segment(string name, string slug) =>
        Assert.Equal(slug, BlizzardApi.Slug(name));
}

public class BlizzardClientStoreTests
{
    [Fact]
    public void The_client_is_kept_encrypted_read_back_and_removed()
    {
        using var t = new TempInstall();
        var dir = Path.Combine(t.Root, "data");
        Assert.Null(BlizzardClientStore.Load(dir, out var none));
        Assert.False(none);

        BlizzardClientStore.Save(dir, FakeBattleNet.Client);
        var bytes = File.ReadAllBytes(BlizzardClientStore.PathIn(dir));
        Assert.DoesNotContain(FakeBattleNet.Secret, Encoding.UTF8.GetString(bytes));
        Assert.DoesNotContain("client-id-42", Encoding.UTF8.GetString(bytes));
        Assert.Equal(FakeBattleNet.Client, BlizzardClientStore.Load(dir, out var unreadable));
        Assert.False(unreadable);

        BlizzardClientStore.Remove(dir);
        Assert.False(File.Exists(BlizzardClientStore.PathIn(dir)));
    }

    [Fact]
    public void A_file_this_user_cannot_decrypt_is_unreadable_not_a_failure()
    {
        using var t = new TempInstall();
        var dir = Path.Combine(t.Root, "data");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(BlizzardClientStore.PathIn(dir), [1, 2, 3, 4, 5, 6, 7, 8]);
        Assert.Null(BlizzardClientStore.Load(dir, out var unreadable));
        Assert.True(unreadable);
    }
}

public class ArmoryApiWorkerTests
{
    private const string A = "Player-6796-000A598A";
    private const long ANumber = 678282;
    private const string B = "Player-6796-00381101";
    private static readonly string AUrl = Armory.Url(A, false)!;
    private static long DaysAgo(int days) => DateTimeOffset.UtcNow.AddDays(-days).ToUnixTimeSeconds();
    private static readonly DateTimeOffset Made = DateTimeOffset.UtcNow.AddHours(-1);

    private static Controller Started(TempInstall t, FakeCdn cdn, Func<DateTime>? clock = null, bool saved = true)
    {
        if (saved) BlizzardClientStore.Save(ArmoryWorkerTests.Data(t), FakeBattleNet.Client);
        return ArmoryWorkerTests.Started(t, cdn, clock: clock);
    }

    private static int Renders(FakeCdn cdn) => FakeBattleNet.Count(cdn, Armory.Host);
    private static int Profiles(FakeCdn cdn) => FakeBattleNet.Count(cdn, BlizzardApi.ApiHost);

    [Fact]
    public void A_character_the_api_says_is_level_ten_is_drawn_although_this_pc_saw_it_at_nine()
    {
        using var t = new TempInstall();
        ArmoryWorkerTests.Roster(t, level: 9, updated: DaysAgo(3));
        var cdn = new FakeCdn
        {
            Respond = r => FakeBattleNet.IsToken(r) ? FakeBattleNet.TokenOk()
                : FakeBattleNet.IsApi(r) ? FakeBattleNet.Profile(ANumber, 10)
                : FakeCdn.Png(FakeCdn.RenderPng(150), "\"v1\"", Made),
        };
        using var c = Started(t, cdn);
        var folder = new CutoutFolder(t.Install.CutoutAddonDir);
        Until(() => folder.ArmoryRenders().ContainsKey(A), "the render of a character this PC saw at level 9");
        lock (cdn.Requests) Assert.Equal(AUrl, cdn.Requests.Last(q => new Uri(q.Url).Host == Armory.Host).Url);
        Assert.Equal(BlizzardClientState.Saved, c.Current.BlizzardClient);
        c.CheckArmoryNow();                                           // the API too, at once
        Until(() => Profiles(cdn) == 2, "the API asked again");

        // Nothing secret in the log.
        var log = File.ReadAllText(c.LogPath);
        Assert.DoesNotContain(FakeBattleNet.Secret, log);
        Assert.DoesNotContain(FakeBattleNet.Token, log);
        Assert.DoesNotContain("client-id-42", log);
    }

    [Fact]
    public void Without_a_client_a_level_nine_is_not_asked_about_anywhere()
    {
        using var t = new TempInstall();
        ArmoryWorkerTests.Roster(t, level: 9, updated: DaysAgo(3));
        var cdn = new FakeCdn();
        using var c = Started(t, cdn, saved: false);
        Until(() => c.Current.Shell.Report is not null, "the first pass");
        Thread.Sleep(1500);
        Assert.Equal(0, cdn.Count);
        Assert.Equal(BlizzardClientState.None, c.Current.BlizzardClient);
    }

    [Fact]
    public void A_login_the_api_knows_opens_the_hours_after_play()
    {
        using var t = new TempInstall();
        ArmoryWorkerTests.Roster(t, updated: DaysAgo(3));          // this PC saw it played 3 days ago
        var shift = TimeSpan.Zero;
        var login = DateTimeOffset.UtcNow.AddHours(-10);              // played elsewhere, 10 hours ago
        var cdn = new FakeCdn
        {
            Respond = r => FakeBattleNet.IsToken(r) ? FakeBattleNet.TokenOk()
                : FakeBattleNet.IsApi(r) ? FakeBattleNet.Profile(ANumber, 25, login)
                : new HttpResponseMessage(HttpStatusCode.Forbidden),
        };
        using var c = Started(t, cdn, clock: () => DateTime.Now + shift);
        Until(() => Renders(cdn) == 1, "the look at the start");
        shift = Armory.CheckEvery + TimeSpan.FromMinutes(1);
        c.SetArmoryRemake(8, inDays: true);                                     // wakes the worker, as its minute's tick would
        Until(() => Renders(cdn) == 2, "the hourly look, from the API's login");
        Assert.Equal(1, Profiles(cdn));                               // the API itself: every 6 hours
    }

    [Fact]
    public void A_character_hidden_while_the_api_is_asked_about_another_is_not_sent()
    {
        using var t = new TempInstall();
        ArmoryWorkerTests.Roster(t, updated: DaysAgo(3), also: (B, "Zoruka Sumner", false));
        using var held = new ManualResetEventSlim();
        var cdn = new FakeCdn
        {
            Respond = r =>
            {
                if (FakeBattleNet.IsToken(r)) return FakeBattleNet.TokenOk();
                if (!FakeBattleNet.IsApi(r)) return new HttpResponseMessage(HttpStatusCode.Forbidden);
                if (r.RequestUri!.AbsolutePath.EndsWith("/karuzo-macphisto", StringComparison.Ordinal)) held.Wait(TimeSpan.FromSeconds(10));
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            },
        };
        using var c = Started(t, cdn);
        Until(() => Profiles(cdn) == 1, "Karuzo asked first");
        ArmoryWorkerTests.Roster(t, updated: DaysAgo(3), also: (B, "Zoruka Sumner", true));
        held.Set();
        Until(() => Renders(cdn) >= 1, "the CDN look after the API");
        Thread.Sleep(1500);
        lock (cdn.Requests) Assert.DoesNotContain(cdn.Requests, q => q.Url.Contains("zoruka", StringComparison.Ordinal) || q.Url.Contains("381101", StringComparison.Ordinal));
    }

    [Fact]
    public void A_client_removed_while_it_is_asked_leaves_nothing_of_its_answer()
    {
        using var t = new TempInstall();
        ArmoryWorkerTests.Roster(t, level: 9, updated: DaysAgo(3));
        using var held = new ManualResetEventSlim();
        var cdn = new FakeCdn
        {
            Respond = r =>
            {
                if (FakeBattleNet.IsToken(r)) return FakeBattleNet.TokenOk();
                if (!FakeBattleNet.IsApi(r)) return FakeCdn.Png(FakeCdn.RenderPng(150), "\"v1\"", Made);
                held.Wait(TimeSpan.FromSeconds(10));
                return FakeBattleNet.Profile(ANumber, 10);
            },
        };
        using var c = Started(t, cdn);
        Until(() => Profiles(cdn) == 1, "the API asked");
        c.RemoveBlizzardClient();
        held.Set();
        Thread.Sleep(2500);
        Assert.Equal(0, Renders(cdn));                                // its level 10 was not kept
        Assert.False(File.Exists(BlizzardClientStore.PathIn(ArmoryWorkerTests.Data(t))));
        Assert.Equal(BlizzardClientState.None, c.Current.BlizzardClient);
    }

    [Fact]
    public async Task A_refused_client_is_said_the_armory_carries_on_without_it_and_a_test_that_passes_takes_it_back()
    {
        using var t = new TempInstall();
        ArmoryWorkerTests.Roster(t, updated: DaysAgo(3), also: (B, "Zoruka Sumner", false));
        var refuse = true;
        var cdn = new FakeCdn
        {
            Respond = r => FakeBattleNet.IsToken(r) ? (refuse ? new HttpResponseMessage(HttpStatusCode.Unauthorized) : FakeBattleNet.TokenOk())
                : FakeBattleNet.IsApi(r) ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.Forbidden),
        };
        using var c = Started(t, cdn);
        Until(() => c.Current.BlizzardClient == BlizzardClientState.Refused, "the refusal");
        Until(() => Renders(cdn) == 2, "both characters asked of the CDN");
        c.SetArmoryRemake(8, inDays: true);                                     // another look: the API stays off
        Thread.Sleep(1500);
        Assert.Equal(1, FakeBattleNet.Count(cdn, BlizzardApi.TokenHost));
        Assert.Equal(0, Profiles(cdn));

        refuse = false;
        Assert.Equal("Battle.net accepted the client.", await c.TestBlizzardClientAsync());
        Assert.Equal(BlizzardClientState.Saved, c.Current.BlizzardClient);
        Until(() => Profiles(cdn) == 2, "both characters asked of the API");
    }

    [Fact]
    public void A_busy_api_holds_the_cdn_too()
    {
        using var t = new TempInstall();
        ArmoryWorkerTests.Roster(t, updated: DaysAgo(3));
        var shift = TimeSpan.Zero;
        var busy = true;
        var cdn = new FakeCdn
        {
            Respond = r => FakeBattleNet.IsToken(r) ? FakeBattleNet.TokenOk()
                : FakeBattleNet.IsApi(r) ? (busy ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) : FakeBattleNet.Profile(ANumber, 25))
                : new HttpResponseMessage(HttpStatusCode.Forbidden),
        };
        using var c = Started(t, cdn, clock: () => DateTime.Now + shift);
        Until(() => Profiles(cdn) == 1, "the API asked");
        c.SetArmoryRemake(8, inDays: true);
        Thread.Sleep(1500);
        Assert.Equal(0, Renders(cdn));                                // everyone waits
        busy = false;
        shift = ArmoryClient.DefaultBusy + TimeSpan.FromMinutes(1);
        c.SetArmoryRemake(9, inDays: true);
        Until(() => Renders(cdn) == 1, "the CDN after the wait");
        Assert.Equal(2, Profiles(cdn));                               // and the busy one asked again
    }

    [Fact]
    public void A_saved_client_this_user_cannot_read_is_said_and_the_armory_carries_on()
    {
        using var t = new TempInstall();
        ArmoryWorkerTests.Roster(t, updated: DaysAgo(3));
        Directory.CreateDirectory(ArmoryWorkerTests.Data(t));
        File.WriteAllBytes(BlizzardClientStore.PathIn(ArmoryWorkerTests.Data(t)), [9, 9, 9, 9]);
        var cdn = new FakeCdn();
        using var c = Started(t, cdn, saved: false);
        Until(() => Renders(cdn) == 1, "the CDN look");
        Assert.Equal(BlizzardClientState.Unreadable, c.Current.BlizzardClient);
        Assert.Equal(1, cdn.Count);
    }

    [Fact]
    public async Task Save_and_test_a_client_and_diagnostics_say_only_that_there_is_one()
    {
        using var t = new TempInstall();
        ArmoryWorkerTests.Roster(t, updated: DaysAgo(3));
        var cdn = new FakeCdn
        {
            Respond = r => FakeBattleNet.IsToken(r) ? FakeBattleNet.TokenOk()
                : FakeBattleNet.IsApi(r) ? FakeBattleNet.Profile(ANumber, 25)
                : new HttpResponseMessage(HttpStatusCode.Forbidden),
        };
        using var c = Started(t, cdn, saved: false);
        Until(() => Renders(cdn) == 1, "the CDN look without a client");
        Assert.NotNull(c.SetBlizzardClient(" ", FakeBattleNet.Secret));
        Assert.Null(c.SetBlizzardClient(" client-id-42 ", FakeBattleNet.Secret + " "));
        Assert.Equal(FakeBattleNet.Client, BlizzardClientStore.Load(ArmoryWorkerTests.Data(t), out _));
        Until(() => Profiles(cdn) == 1, "the API asked once the client is saved");
        Assert.Equal("Battle.net accepted the client.", await c.TestBlizzardClientAsync());

        var file = c.SaveDiagnostics(Path.Combine(t.Root, "out"), t.Root);
        var text = File.ReadAllText(file);
        Assert.Contains("Blizzard API client saved", text);
        Assert.DoesNotContain(FakeBattleNet.Secret, text);
        Assert.DoesNotContain("client-id-42", text);
        Assert.DoesNotContain(FakeBattleNet.Token, text);
    }

    [Fact]
    public void A_character_hidden_or_a_client_removed_while_a_token_is_asked_for_is_not_sent()
    {
        // Hidden while the token is on its way: that character is not named; the next one is asked.
        using (var t = new TempInstall())
        {
            ArmoryWorkerTests.Roster(t, updated: DaysAgo(3), also: (B, "Zoruka Sumner", false));
            using var held = new ManualResetEventSlim();
            var cdn = new FakeCdn
            {
                Respond = r =>
                {
                    if (FakeBattleNet.IsToken(r)) { held.Wait(TimeSpan.FromSeconds(10)); return FakeBattleNet.TokenOk(); }
                    return FakeBattleNet.IsApi(r) ? new HttpResponseMessage(HttpStatusCode.NotFound) : new HttpResponseMessage(HttpStatusCode.Forbidden);
                },
            };
            using var c = Started(t, cdn);
            Until(() => FakeBattleNet.Count(cdn, BlizzardApi.TokenHost) == 1, "the token asked for");
            ArmoryWorkerTests.Roster(t, updated: DaysAgo(3), hidden: true, also: (B, "Zoruka Sumner", false));
            held.Set();
            Until(() => Profiles(cdn) == 1, "Zoruka asked");
            Thread.Sleep(1000);
            lock (cdn.Requests) Assert.DoesNotContain(cdn.Requests, q => q.Url.Contains("karuzo", StringComparison.Ordinal) || q.Url.Contains("678282", StringComparison.Ordinal));
        }

        // Removed while the token is on its way: nothing is asked with it.
        using (var t = new TempInstall())
        {
            ArmoryWorkerTests.Roster(t, updated: DaysAgo(3));
            using var held = new ManualResetEventSlim();
            var cdn = new FakeCdn
            {
                Respond = r =>
                {
                    if (FakeBattleNet.IsToken(r)) { held.Wait(TimeSpan.FromSeconds(10)); return FakeBattleNet.TokenOk(); }
                    return FakeBattleNet.IsApi(r) ? FakeBattleNet.Profile(ANumber, 25) : new HttpResponseMessage(HttpStatusCode.Forbidden);
                },
            };
            using var c = Started(t, cdn);
            Until(() => FakeBattleNet.Count(cdn, BlizzardApi.TokenHost) == 1, "the token asked for");
            Assert.Null(c.RemoveBlizzardClient());
            held.Set();
            Until(() => Renders(cdn) == 1, "the CDN look after the API");
            Assert.Equal(0, Profiles(cdn));
        }

        // The armory turned off while the token is on its way: nothing is asked with it.
        using (var t = new TempInstall())
        {
            ArmoryWorkerTests.Roster(t, updated: DaysAgo(3));
            using var held = new ManualResetEventSlim();
            var cdn = new FakeCdn
            {
                Respond = r =>
                {
                    if (FakeBattleNet.IsToken(r)) { held.Wait(TimeSpan.FromSeconds(10)); return FakeBattleNet.TokenOk(); }
                    return FakeBattleNet.IsApi(r) ? FakeBattleNet.Profile(ANumber, 25) : new HttpResponseMessage(HttpStatusCode.Forbidden);
                },
            };
            using var c = Started(t, cdn);
            Until(() => FakeBattleNet.Count(cdn, BlizzardApi.TokenHost) == 1, "the token asked for");
            c.SetArmory(false);
            held.Set();
            Thread.Sleep(1500);
            Assert.Equal(1, cdn.Count);                               // the token, and nothing after it
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_401_after_the_client_is_removed_or_replaced_sends_nothing_more_with_the_old_one(bool replace)
    {
        using var t = new TempInstall();
        ArmoryWorkerTests.Roster(t, updated: DaysAgo(3));
        using var held = new ManualResetEventSlim();
        var old = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("client-id-42:" + FakeBattleNet.Secret));
        var first = true;
        var cdn = new FakeCdn
        {
            Respond = r =>
            {
                if (FakeBattleNet.IsToken(r)) return FakeBattleNet.TokenOk();
                if (!FakeBattleNet.IsApi(r)) return new HttpResponseMessage(HttpStatusCode.Forbidden);
                if (!first) return FakeBattleNet.Profile(ANumber, 25);
                first = false;
                held.Wait(TimeSpan.FromSeconds(10));
                return new HttpResponseMessage(HttpStatusCode.Unauthorized);
            },
        };
        using var c = Started(t, cdn);
        Until(() => Profiles(cdn) == 1, "the profile asked");
        if (replace) Assert.Null(c.SetBlizzardClient("another-id", "another-secret"));
        else Assert.Null(c.RemoveBlizzardClient());
        held.Set();
        if (replace) Until(() => Profiles(cdn) == 2, "the new client asked");
        else Until(() => Renders(cdn) == 1, "the CDN look after the API");
        Thread.Sleep(500);
        // One token with the old client, the first: none after its 401.
        int oldTokens, newTokens;
        lock (cdn.Requests)
        {
            var tokens = cdn.Requests.Select((q, i) => (q.Url, Auth: cdn.Auth[i])).Where(q => new Uri(q.Url).Host == BlizzardApi.TokenHost).ToList();
            oldTokens = tokens.Count(q => q.Auth == old);
            newTokens = tokens.Count(q => q.Auth != old);
        }
        Assert.Equal(1, oldTokens);
        Assert.Equal(replace ? 1 : 0, newTokens);
    }

    [Fact]
    public void A_client_file_that_will_not_be_deleted_is_not_used_now_or_at_the_next_start()
    {
        using var t = new TempInstall();
        ArmoryWorkerTests.Roster(t, updated: DaysAgo(3));
        var cdn = new FakeCdn
        {
            Respond = r => FakeBattleNet.IsToken(r) ? FakeBattleNet.TokenOk()
                : FakeBattleNet.IsApi(r) ? FakeBattleNet.Profile(ANumber, 25)
                : new HttpResponseMessage(HttpStatusCode.Forbidden),
        };
        var file = BlizzardClientStore.PathIn(ArmoryWorkerTests.Data(t));
        using (var c = Started(t, cdn))
        {
            Until(() => Renders(cdn) == 1, "the first look, with the API");
            File.SetAttributes(file, FileAttributes.ReadOnly);
            Assert.Equal(PassText.BlizzardState(BlizzardClientState.NotDeleted), c.RemoveBlizzardClient());
            Assert.True(File.Exists(file));
            Assert.Equal(BlizzardClientState.NotDeleted, c.Current.BlizzardClient);
            Assert.True(Settings.Load(ArmoryWorkerTests.Data(t)).BlizzardRemoved);
            c.CheckArmoryNow();
            Until(() => Renders(cdn) == 2, "a look without it");
            Assert.Equal(1, Profiles(cdn));
        }

        Controller Restart()
        {
            var c = new Controller(new StartupOptions(DataDir: ArmoryWorkerTests.Data(t)), () => throw new InvalidOperationException("detection was used"),
                armoryHttp: () => cdn);
            c.Start();
            return c;
        }
        // The next start does not take it back while the file stays.
        using (var c = Restart())
        {
            Until(() => Renders(cdn) == 3, "the look at the next start");
            Assert.Equal(BlizzardClientState.NotDeleted, c.Current.BlizzardClient);
            Assert.Equal(1, Profiles(cdn));
            Assert.Contains("Blizzard API client removed, its file not deleted", File.ReadAllText(c.SaveDiagnostics(Path.Combine(t.Root, "out"), t.Root)));
        }
        // Once it can be deleted, the next start deletes it.
        File.SetAttributes(file, FileAttributes.Normal);
        using (var c = Restart())
        {
            Until(() => !File.Exists(file), "the file deleted at the start");
            Assert.Equal(BlizzardClientState.None, c.Current.BlizzardClient);
            Assert.False(Settings.Load(ArmoryWorkerTests.Data(t)).BlizzardRemoved);

            // Saving a client, or a Remove that deletes the file, ends it too.
            bool Removed() => Settings.Load(ArmoryWorkerTests.Data(t)).BlizzardRemoved;
            void NotDeleted()
            {
                File.SetAttributes(file, FileAttributes.ReadOnly);
                Assert.NotNull(c.RemoveBlizzardClient());
                File.SetAttributes(file, FileAttributes.Normal);
                Assert.True(Removed());
            }
            Assert.Null(c.SetBlizzardClient(FakeBattleNet.Client.Id, FakeBattleNet.Client.Secret));
            NotDeleted();
            Assert.Null(c.SetBlizzardClient(FakeBattleNet.Client.Id, FakeBattleNet.Client.Secret));
            Assert.False(Removed());
            Assert.Equal(BlizzardClientState.Saved, c.Current.BlizzardClient);
            NotDeleted();
            Assert.Null(c.RemoveBlizzardClient());
            Assert.False(Removed());
            Assert.False(File.Exists(file));
            Assert.Equal(BlizzardClientState.None, c.Current.BlizzardClient);
        }
    }

    [Fact]
    public async Task A_busy_answer_to_test_holds_the_api_and_the_cdn()
    {
        using var t = new TempInstall();
        ArmoryWorkerTests.Roster(t, updated: DaysAgo(3));
        var shift = TimeSpan.Zero;
        var busy = false;
        var login = DateTimeOffset.UtcNow.AddHours(-10);              // the hourly look is due
        var cdn = new FakeCdn
        {
            Respond = r =>
            {
                if (FakeBattleNet.IsToken(r))
                {
                    if (!busy) return FakeBattleNet.TokenOk();
                    var wait = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                    wait.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromHours(2));
                    return wait;
                }
                return FakeBattleNet.IsApi(r) ? FakeBattleNet.Profile(ANumber, 25, login) : new HttpResponseMessage(HttpStatusCode.Forbidden);
            },
        };
        using var c = Started(t, cdn, clock: () => DateTime.Now + shift);
        Until(() => Renders(cdn) == 1, "the look at the start");
        busy = true;
        Assert.StartsWith("Battle.net could not be asked", await c.TestBlizzardClientAsync());
        shift = Armory.CheckEvery + TimeSpan.FromMinutes(1);
        c.SetArmoryRemake(8, inDays: true);                                     // wakes the worker: the hour is due, the wait is not over
        Thread.Sleep(1500);
        Assert.Equal(1, Renders(cdn));
        shift = TimeSpan.FromHours(2) + TimeSpan.FromMinutes(1);
        c.SetArmoryRemake(9, inDays: true);
        Until(() => Renders(cdn) == 2, "the CDN once the wait is over");
    }

    private static void Until(Func<bool> happened, string what) => ArmoryWorkerTests.Until(happened, what);
}
