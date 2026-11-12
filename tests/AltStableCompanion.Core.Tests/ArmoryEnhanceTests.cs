using AltStableCompanion.Core;

namespace AltStableCompanion.Core.Tests;

/// <summary>Renders and enhanced pictures together: the hold, at the fetch and at the launch.</summary>
public class ArmoryEnhanceTests
{
    private const string A = "Player-6796-000A598A";
    private const string Base = "karuzo-macphisto";

    internal sealed class FakeCodex
    {
        public int Calls;
        public string? Failure;
        public TaskCompletionSource<bool>? Hold;

        public EnhanceHooks Hooks => new(async (job, dir, ct) =>
        {
            Interlocked.Increment(ref Calls);
            if (Hold is { } hold)
            {
                using var reg = ct.Register(() => hold.TrySetCanceled());
                try { await hold.Task; }
                catch (OperationCanceledException) { return new CodexResult(null, "cancelled", ""); }
            }
            return Failure is { } f ? new CodexResult(null, f, "") : new CodexResult(Picture(), null, "");
        }, _ => Task.FromResult<string?>("Logged in using ChatGPT"));

        private static byte[] Picture()
        {
            var img = new RgbaImage(200, 400);
            for (var y = 40; y < 400; y++)
                for (var x = 60; x < 140; x++)
                    img[x, y] = (150, 60, 200, 255);
            return PngCodec.Write(img);
        }
    }

    private const string Db = "AltStableDB = {\n[\"" + A + "\"] = {\n[\"name\"] = \"Karuzo Macphisto\",\n[\"class\"] = \"WARLOCK\",\n[\"race\"] = \"Troll\",\n[\"gender\"] = \"Female\",\n[\"level\"] = 25,\n[\"lastUpdate\"] = 1794370273,\n},\n}\n";

    private static void Setup(TempInstall t)
    {
        t.WriteStore("1#1", Db + "AltStablePortraits = nil\n");
        Directory.CreateDirectory(Path.Combine(t.Install.FlavorDir, "WTF"));
        File.WriteAllText(Path.Combine(t.Install.FlavorDir, "WTF", "Config.wtf"), "SET portal \"US\"\n");
        var toc = Path.Combine(t.Install.AddOnsDir, "AltStableRoster", "AltStableRoster.toc");
        Directory.CreateDirectory(Path.GetDirectoryName(toc)!);
        File.WriteAllText(toc, "## X-AltStable-Enhanced: 1\n");
    }

    // Another render, arrived on disk as the worker writes one.
    private static void NewRender(CutoutFolder folder, byte shade)
    {
        var cut = Armory.ToCutout(PngCodec.Read(FakeCdn.RenderPng(shade, h: 650)), new CutoutMeta
        {
            Guid = A, Url = Armory.Url(A, false), LastModified = DateTimeOffset.UtcNow.ToString("R"),
        });
        var ms = new MemoryStream();
        TgaCodec.Write(ms, cut.Canvas);
        folder.WriteArmory(Base, ms.ToArray(), cut.Meta);
    }

    private static Attempt Last(CutoutFolder folder) => AttemptHistory.Load(folder.EnhancedDir, A).Last!;

    private static FakeCdn Cdn(DateTimeOffset? made = null) =>
        new() { Respond = _ => FakeCdn.Png(FakeCdn.RenderPng(150), "\"v1\"", made ?? DateTimeOffset.UtcNow) };

    [Fact]
    public void A_render_is_enhanced_once_then_neither_fetched_nor_enhanced_again_for_n_days()
    {
        using var t = new TempInstall();
        Setup(t);
        var cdn = Cdn();
        var codex = new FakeCodex();
        using var c = ArmoryWorkerTests.Started(t, cdn, enhance: true, hooks: codex.Hooks);
        var folder = new CutoutFolder(t.Install.CutoutAddonDir);
        ArmoryWorkerTests.Until(() => folder.Inventory(PortraitJudge.Unknown).SingleOrDefault()?.Enhanced is not null, "the render's enhanced picture");
        Assert.Equal(1, codex.Calls);
        Assert.Equal((Attempt.FromArmory, (bool?)null), (Last(folder).Source, Last(folder).Confirmed));

        // The armory is not asked again while the picture is under 7 days old...
        var asked = cdn.Count;
        c.CheckArmoryNow();
        ArmoryWorkerTests.Until(() => c.Current.Shell.Report?.Portraits?.Single().ArmoryHeldUntil is not null, "the row's hold");
        Assert.Equal(asked, cdn.Count);
        // ...and a render that arrived anyway (fetched with enhancement off, say) waits too.
        NewRender(folder, 60);
        c.ConvertNow();
        Thread.Sleep(2500);
        Assert.Equal(1, codex.Calls);
        Assert.Null(folder.Inventory(PortraitJudge.Unknown).Single().Enhanced);          // the plain render shows meanwhile
    }

    [Fact]
    public void Check_now_asks_the_armory_past_the_hold_and_the_picture_it_leaves_is_enhanced_at_once()
    {
        using var t = new TempInstall();
        Setup(t);
        byte shade = 150;
        var notModified = false;
        var cdn = new FakeCdn
        {
            Respond = _ => notModified ? new HttpResponseMessage(System.Net.HttpStatusCode.NotModified)
                : FakeCdn.Png(FakeCdn.RenderPng(shade), $"\"v{shade}\"", DateTimeOffset.UtcNow),
        };
        var codex = new FakeCodex();
        using var c = ArmoryWorkerTests.Started(t, cdn, enhance: true, hooks: codex.Hooks);
        var folder = new CutoutFolder(t.Install.CutoutAddonDir);
        PortraitRow Row() => c.Current.Shell.Report!.Portraits!.Single();
        ArmoryWorkerTests.Until(() => codex.Calls == 1 && Last(folder).Ended is not null, "the first picture");

        // Held: the row says until when, to the minute; the armory is not asked.
        c.CheckArmoryNow();
        ArmoryWorkerTests.Until(() => c.Current.Shell.Report?.Portraits?.Single().ArmoryHeldUntil is not null, "the row's hold");
        Assert.Equal(Last(folder).Started.AddDays(7), Row().ArmoryHeldUntil);
        Thread.Sleep(1500);
        var asked = cdn.Count;

        // "Check now": asked at once, past the hold, and the new picture made at once, as asked for.
        shade = 60;
        c.CheckArmoryNow(A);
        ArmoryWorkerTests.Until(() => cdn.Count == asked + 1, "the armory asked past the hold");
        ArmoryWorkerTests.Until(() => codex.Calls == 2 && Last(folder).Ended is not null, "the new picture, at once");
        Assert.Equal((Attempt.FromArmory, (bool?)true), (Last(folder).Source, Last(folder).Confirmed));
        c.CheckArmoryNow();                                           // the next look, as its minute's tick would
        ArmoryWorkerTests.Until(() => Row().ArmoryHeldUntil == Last(folder).Started.AddDays(7), "the hold again, from the new picture");

        // Spent: a render that arrives anyway waits for the hold, as before.
        NewRender(folder, 30);
        c.ConvertNow();
        Thread.Sleep(2500);
        Assert.Equal(2, codex.Calls);
        // "Check now" with nothing newer at the armory (304): the render waiting is the one asked for.
        notModified = true;
        c.CheckArmoryNow(A);
        ArmoryWorkerTests.Until(() => codex.Calls == 3, "the waiting render, enhanced at once");
        Assert.True(Last(folder).Confirmed);
    }

    [Fact]
    public void Check_now_is_one_picture_a_failed_one_and_another_style_after_it_wait_for_the_hold()
    {
        // Codex on #71: the yes outlived its picture; a style change after a failure made a new
        // signature, and it was spent again.
        using var t = new TempInstall();
        Setup(t);
        byte shade = 150;
        var cdn = new FakeCdn { Respond = _ => FakeCdn.Png(FakeCdn.RenderPng(shade), $"\"v{shade}\"", DateTimeOffset.UtcNow) };
        var codex = new FakeCodex();
        using var c = ArmoryWorkerTests.Started(t, cdn, enhance: true, hooks: codex.Hooks);
        var folder = new CutoutFolder(t.Install.CutoutAddonDir);
        ArmoryWorkerTests.Until(() => codex.Calls == 1 && Last(folder).Ended is not null, "the first picture");

        codex.Failure = "no picture";
        shade = 60;
        c.CheckArmoryNow(A);
        ArmoryWorkerTests.Until(() => codex.Calls == 2 && Last(folder).Ended is not null, "the asked-for picture, failed");
        Assert.True(Last(folder).Confirmed);

        codex.Failure = null;
        c.SetEnhance(true, 10, EnhanceStyles.Realistic);
        Thread.Sleep(2500);
        Assert.Equal(2, codex.Calls);
        // Another press is another yes.
        c.CheckArmoryNow(A);
        ArmoryWorkerTests.Until(() => codex.Calls == 3, "the picture asked for again");
    }

    [Fact]
    public void A_failed_armory_picture_holds_the_next_one_as_a_made_one_does()
    {
        using var t = new TempInstall();
        Setup(t);
        var codex = new FakeCodex { Failure = "no picture" };
        using var c = ArmoryWorkerTests.Started(t, Cdn(), enhance: true, hooks: codex.Hooks);
        var folder = new CutoutFolder(t.Install.CutoutAddonDir);
        ArmoryWorkerTests.Until(() => File.Exists(AttemptHistory.PathFor(folder.EnhancedDir, A)) && Last(folder).Ended is not null, "the failed attempt");
        codex.Failure = null;
        NewRender(folder, 60);
        c.ConvertNow();
        Thread.Sleep(2500);
        Assert.Equal(1, codex.Calls);
    }

    [Fact]
    public async Task A_picture_cancelled_by_a_quit_is_not_tried_again_inside_n_days_and_is_after()
    {
        using var t = new TempInstall();
        Setup(t);
        var cdn = Cdn();
        var folder = new CutoutFolder(t.Install.CutoutAddonDir);
        var first = new FakeCodex { Hold = new TaskCompletionSource<bool>() };
        using (var c = ArmoryWorkerTests.Started(t, cdn, enhance: true, hooks: first.Hooks))
        {
            ArmoryWorkerTests.Until(() => first.Calls == 1, "the launch");
            await c.StopAsync();
        }
        Assert.StartsWith(Attempt.Cancelled, Last(folder).Outcome);

        // A cancel is worth one more try - but not inside the hold.
        var second = new FakeCodex();
        using (var c = ArmoryWorkerTests.Started(t, cdn, enhance: true, hooks: second.Hooks))
        {
            ArmoryWorkerTests.Until(() => c.Current.Shell.Report is not null && c.Current.CodexProbed, "the start");
            Thread.Sleep(2500);
            Assert.Equal(0, second.Calls);
            await c.StopAsync();
        }

        // Ten days later (the record moved back): the try it was owed.
        var path = AttemptHistory.PathFor(folder.EnhancedDir, A);
        var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!;
        foreach (var a in json["Attempts"]!.AsArray()) a!["Started"] = DateTime.Now.AddDays(-10);
        File.WriteAllText(path, json.ToJsonString());
        var third = new FakeCodex();
        using (var c = ArmoryWorkerTests.Started(t, cdn, enhance: true, hooks: third.Hooks))
        {
            ArmoryWorkerTests.Until(() => third.Calls == 1, "the retry at expiry");
            ArmoryWorkerTests.Until(() => folder.Inventory(PortraitJudge.Unknown).SingleOrDefault()?.Enhanced is not null, "the picture");
        }
    }

    [Fact]
    public void Turned_on_after_the_render_came_its_first_picture_is_made_and_starts_the_hold()
    {
        using var t = new TempInstall();
        Setup(t);
        var codex = new FakeCodex();
        using var c = ArmoryWorkerTests.Started(t, Cdn(), enhance: false, hooks: codex.Hooks);
        var folder = new CutoutFolder(t.Install.CutoutAddonDir);
        ArmoryWorkerTests.Until(() => folder.ArmoryRenders().ContainsKey(A), "the render");
        Thread.Sleep(1000);
        Assert.Equal(0, codex.Calls);
        c.SetEnhance(true, 10, EnhanceStyles.WowLike);
        ArmoryWorkerTests.Until(() => folder.Inventory(PortraitJudge.Unknown).SingleOrDefault()?.Enhanced is not null, "the picture");
        Assert.Equal(Attempt.FromArmory, Last(folder).Source);
        Assert.NotNull(Armory.HeldUntil(AttemptHistory.Load(folder.EnhancedDir, A), 7, DateTime.Now));
    }

    [Fact]
    public void A_render_drawn_again_that_looks_the_same_keeps_its_file_and_its_enhanced_picture()
    {
        using var t = new TempInstall();
        Setup(t);
        var cdn = Cdn();
        var codex = new FakeCodex();
        using var c = ArmoryWorkerTests.Started(t, cdn, enhance: true, hooks: codex.Hooks, days: 1);
        var folder = new CutoutFolder(t.Install.CutoutAddonDir);
        ArmoryWorkerTests.Until(() => folder.Inventory(PortraitJudge.Unknown).SingleOrDefault()?.Enhanced is not null, "the picture");
        var tga = Path.Combine(folder.ArmoryDir, Base + ".tga");
        var bytes = File.ReadAllBytes(tga);

        // The hold is over (the record moved back two days): the armory is asked again, and
        // answers with the same picture, two levels brighter - other bytes, the same to the eye.
        var path = AttemptHistory.PathFor(folder.EnhancedDir, A);
        var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!;
        foreach (var a in json["Attempts"]!.AsArray()) a!["Started"] = DateTime.Now.AddDays(-2);
        File.WriteAllText(path, json.ToJsonString());
        cdn.Respond = _ => FakeCdn.Png(FakeCdn.RenderPng(152), "\"v2\"", DateTimeOffset.UtcNow);
        c.CheckArmoryNow();
        ArmoryWorkerTests.Until(() => folder.ReadArmoryMeta(Base)?.ETag == "\"v2\"", "the new date");
        Thread.Sleep(2000);
        Assert.Equal(bytes, File.ReadAllBytes(tga));
        Assert.NotNull(folder.Inventory(PortraitJudge.Unknown).Single().Enhanced);
        Assert.Equal(1, codex.Calls);

        // Another look: drawn again, changed.
        cdn.Respond = _ => FakeCdn.Png(FakeCdn.RenderPng(60), "\"v3\"", DateTimeOffset.UtcNow);
        c.CheckArmoryNow();
        ArmoryWorkerTests.Until(() => codex.Calls == 2, "the new render's picture");
    }

    [Fact]
    public void Not_on_their_own_armory_pictures_are_offered_and_made_when_asked()
    {
        using var t = new TempInstall();
        Setup(t);
        var codex = new FakeCodex();
        var data = ArmoryWorkerTests.Data(t);
        new Settings { Started = true, WowFlavorDir = t.Install.FlavorDir, Armory = true, Enhance = true, ArmoryEnhanceAuto = false }.Save(data);
        using var c = new Controller(new StartupOptions(DataDir: data), () => null, enhance: codex.Hooks, armoryHttp: () => Cdn());
        c.Start();
        ArmoryWorkerTests.Until(() => c.Current.Shell.ArmoryHeld?.SequenceEqual(["Karuzo Macphisto"]) == true, "the offer");
        Assert.Equal(0, codex.Calls);
        Assert.Contains("armory picture is not enhanced yet", PassText.EnhanceHeld(c.Current.Shell.EnhanceHeld, c.Current.Shell.ArmoryHeld));
        Assert.Equal("Make it now (1 generation)", PassText.EnhanceHeldButton(c.Current.Shell.EnhanceHeld, c.Current.Shell.ArmoryHeld));
        c.RemakeEnhanced();
        var folder = new CutoutFolder(t.Install.CutoutAddonDir);
        ArmoryWorkerTests.Until(() => folder.Inventory(PortraitJudge.Unknown).SingleOrDefault()?.Enhanced is not null, "the picture asked for");
        Assert.Equal((Attempt.FromArmory, (bool?)true), (Last(folder).Source, Last(folder).Confirmed));
    }

    [Fact]
    public void The_daily_limit_stops_automatic_armory_pictures_and_can_be_turned_off()
    {
        using var t = new TempInstall();
        const string B = "Player-6796-000A598B";
        t.WriteStore("1#1", Db.Replace("},\n}\n","},\n[\"" + B + "\"] = {\n[\"name\"] = \"Zoruka Sumner\",\n[\"class\"] = \"MAGE\",\n[\"race\"] = \"Gnome\",\n[\"gender\"] = \"Female\",\n[\"level\"] = 25,\n[\"lastUpdate\"] = 1794370273,\n},\n}\n") + "AltStablePortraits = nil\n");
        Setup2(t);
        var codex = new FakeCodex();
        var data = ArmoryWorkerTests.Data(t);
        new Settings { Started = true, WowFlavorDir = t.Install.FlavorDir, Armory = true, Enhance = true, ArmoryDailyMax = 1 }.Save(data);
        using var c = new Controller(new StartupOptions(DataDir: data), () => null, enhance: codex.Hooks, armoryHttp: () => Cdn());
        c.Start();
        var folder = new CutoutFolder(t.Install.CutoutAddonDir);
        ArmoryWorkerTests.Until(() => folder.ArmoryRenders().Count == 2 && codex.Calls == 1, "both renders, one picture");
        c.ConvertNow();
        Thread.Sleep(2500);
        Assert.Equal(1, codex.Calls);
        c.SetArmoryEnhance(auto: true, limit: false, max: 1);
        ArmoryWorkerTests.Until(() => codex.Calls == 2, "the second, the limit off");
    }

    // The rest of Setup, for a roster written by the test.
    private static void Setup2(TempInstall t)
    {
        Directory.CreateDirectory(Path.Combine(t.Install.FlavorDir, "WTF"));
        File.WriteAllText(Path.Combine(t.Install.FlavorDir, "WTF", "Config.wtf"), "SET portal \"US\"\n");
        var toc = Path.Combine(t.Install.AddOnsDir, "AltStableRoster", "AltStableRoster.toc");
        Directory.CreateDirectory(Path.GetDirectoryName(toc)!);
        File.WriteAllText(toc, "## X-AltStable-Enhanced: 1\n");
    }

    [Fact]
    public void A_capture_after_the_render_is_enhanced_at_once_hold_or_not()
    {
        using var t = new TempInstall();
        Setup(t);
        var codex = new FakeCodex();
        using var c = ArmoryWorkerTests.Started(t, Cdn(DateTimeOffset.UtcNow.AddHours(-8)), enhance: true, hooks: codex.Hooks);
        var folder = new CutoutFolder(t.Install.CutoutAddonDir);
        ArmoryWorkerTests.Until(() => codex.Calls == 1 && folder.Inventory(PortraitJudge.Unknown).SingleOrDefault()?.Enhanced is not null, "the render's picture");

        // The player captures: the capture is newer than the render, and is the player's own act.
        var at = DateTime.Now;
        var (black, white) = TestData.Pair(400, 1200, 150, 250, 100, 700);
        t.WriteShot(at, black);
        t.WriteShot(at.AddSeconds(1), white);
        t.WriteStore("1#1", Db + "AltStablePortraits = {\n[\"version\"] = 1,\n[\"renders\"] = {\n"
            + TestData.Record("Karuzo Macphisto", A, 1, at) + ",\n" + TestData.Record("Karuzo Macphisto", A, 2, at.AddSeconds(1)) + ",\n},\n}\n");
        c.ConvertNow();
        ArmoryWorkerTests.Until(() => codex.Calls == 2, "the capture's picture");
        ArmoryWorkerTests.Until(() => folder.Inventory(PortraitJudge.Unknown).SingleOrDefault() is { Folder: null, Enhanced: not null }, "the capture, enhanced, listed");
        Assert.Equal(Attempt.FromCapture, Last(folder).Source);
        Assert.Equal(Base, folder.FileBaseOf(A));                    // the render's name
    }
}
