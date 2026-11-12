using System.Net;
using System.Net.Http.Headers;
using AltStableCompanion.Core;

namespace AltStableCompanion.Core.Tests;

/// <summary>The rules of #63 that need no network: names, dates, who is due, which portrait wins.</summary>
public class ArmoryRuleTests
{
    [Fact]
    public void The_url_is_the_measured_one()
    {
        // Measured 2026-11-11: Karuzo Macphisto, Player-6796-000A598A, the API's id 678282.
        Assert.Equal(678282, Armory.PlayerNumber("Player-6796-000A598A"));
        Assert.Equal("https://render.worldofwarcraft.com/forever-us/character/138/678282-main-raw.png", Armory.Url("Player-6796-000A598A", sd: false));
        Assert.Equal("https://render.worldofwarcraft.com/forever-us/character/138/678282-main-sd-raw.png", Armory.Url("Player-6796-000A598A", sd: true));
        Assert.Equal("https://render.worldofwarcraft.com/forever-us/character/1/3674369-main-raw.png", Armory.Url("Player-6933-00381101", sd: false));
    }

    [Theory]
    [InlineData("Pet-0-1234")]
    [InlineData("Player-1")]
    [InlineData("Player-x-00AA")]
    [InlineData("Player-1-00000000")]
    [InlineData("Player-1-123456789")]
    [InlineData("Player-1-00ZZ")]
    [InlineData("Player-1-00AA/../x")]
    public void Anything_but_a_player_guid_has_no_url(string guid)
    {
        Assert.Null(Armory.PlayerNumber(guid));
        Assert.Null(Armory.Url(guid, sd: false));
    }

    private static CutoutMeta Render(DateTimeOffset made, long? skew = null) =>
        new() { Guid = "Player-1-0000AAAA", LastModified = made.ToString("R"), Skew = skew };

    [Fact]
    public void A_render_is_of_seven_hours_before_its_date_and_a_capture_at_or_after_that_wins()
    {
        var made = new DateTimeOffset(2026, 11, 11, 11, 40, 17, TimeSpan.Zero);
        var asOf = made - TimeSpan.FromHours(7);
        Assert.Equal(asOf, Armory.AsOf(Render(made)));
        Assert.False(Armory.RenderWins(asOf.ToUnixTimeSeconds(), Render(made)));          // at: the capture
        Assert.False(Armory.RenderWins(asOf.ToUnixTimeSeconds() + 60, Render(made)));
        Assert.True(Armory.RenderWins(asOf.ToUnixTimeSeconds() - 1, Render(made)));
        // A capture that says nothing of when: older than any render.
        Assert.True(Armory.RenderWins(null, Render(made)));
        // A render whose date cannot be read is not a primary.
        Assert.False(Armory.RenderWins(null, new CutoutMeta { LastModified = "yesterday" }));
    }

    [Fact]
    public void A_wrong_clock_is_allowed_for_beyond_two_minutes_only()
    {
        var made = new DateTimeOffset(2026, 11, 11, 11, 0, 0, TimeSpan.Zero);
        // The CDN is an hour ahead of this PC: the render is of an hour earlier in this PC's clock.
        Assert.Equal(made - TimeSpan.FromHours(8), Armory.AsOf(Render(made, skew: 3600)));
        Assert.Equal(made - TimeSpan.FromHours(7), Armory.AsOf(Render(made, skew: 90)));
    }

    [Fact]
    public void A_render_is_cut_to_its_figure_and_never_scaled_up()
    {
        var png = new RgbaImage(1600, 1200);
        for (var y = 300; y < 1000; y++)
            for (var x = 700; x < 1030; x++)
                png[x, y] = (10, 20, 30, 255);
        png[100, 100] = (1, 1, 1, 5);                     // at the matte's floor: nothing
        var cut = Armory.ToCutout(png, new CutoutMeta { Guid = "Player-1-0000AAAA" });
        Assert.Equal((330, 700, 512, 1024), (cut.Meta.W, cut.Meta.H, cut.Meta.TexW, cut.Meta.TexH));
        Assert.Equal((10, 20, 30, 255), cut.Canvas[0, 0]);
        Assert.Null(cut.Meta.NativeH);
        Assert.Throws<ThumbnailException>(() => Armory.ToCutout(new RgbaImage(4, 4), new CutoutMeta()));
    }

    [Fact]
    public void Two_renders_are_the_same_picture_when_under_one_percent_of_the_figure_moved()
    {
        static RgbaImage Figure(int changedRows, byte shade = 100)
        {
            var img = new RgbaImage(100, 100);
            for (var y = 0; y < 100; y++)
                for (var x = 0; x < 100; x++)
                {
                    var v = (byte)(y < changedRows ? shade + 100 : shade);
                    img[x, y] = (v, 0, 0, 255);
                }
            return img;
        }
        Assert.True(Armory.NearlySame(Figure(0), Figure(0)));
        Assert.True(Armory.NearlySame(Figure(0), Figure(0, shade: 120)));     // 20 levels everywhere: noise
        Assert.False(Armory.NearlySame(Figure(0), Figure(0, shade: 125)));    // 25: not
        Assert.True(Armory.NearlySame(Figure(0), Figure(0)) && !Armory.NearlySame(Figure(0), Figure(1)));   // 1 % moved: another picture
        Assert.False(Armory.NearlySame(Figure(0), new RgbaImage(100, 50)));
    }

    [Fact]
    public void The_region_is_the_portal_in_config_wtf()
    {
        using var t = new TempInstall();
        Assert.Null(Armory.RegionOf(t.Install));
        Directory.CreateDirectory(Path.Combine(t.Install.FlavorDir, "WTF"));
        File.WriteAllText(Path.Combine(t.Install.FlavorDir, "WTF", "Config.wtf"), "SET locale \"enUS\"\nSET portal \"us\"\n");
        Assert.Equal("US", Armory.RegionOf(t.Install));
        File.WriteAllText(Path.Combine(t.Install.FlavorDir, "WTF", "Config.wtf"), "SET portal \"EU\"\n");
        Assert.Equal("EU", Armory.RegionOf(t.Install));
    }

    private static readonly DateTimeOffset Played = new(2026, 11, 11, 4, 24, 0, TimeSpan.Zero);
    private static readonly ArmoryCharacter Karuzo = new("Player-6796-000A598A", "Karuzo Macphisto", 25, Played);

    [Fact]
    public void Due_once_a_run_then_hourly_from_six_to_twenty_four_hours_after_play()
    {
        // The sweep: not looked at this run.
        Assert.True(ArmoryPlanner.Due(Karuzo, Played.AddDays(-3), checkedThisRun: false, heldBefore: false, Played.AddHours(1)));
        // Looked at, and too soon after play.
        Assert.False(ArmoryPlanner.Due(Karuzo, Played.AddMinutes(1), true, false, Played.AddHours(5)));
        // In the window, an hour since the last look; not yet an hour.
        Assert.True(ArmoryPlanner.Due(Karuzo, Played.AddHours(5), true, false, Played.AddHours(6)));
        Assert.False(ArmoryPlanner.Due(Karuzo, Played.AddHours(6.5), true, false, Played.AddHours(7)));
        Assert.True(ArmoryPlanner.Due(Karuzo, Played.AddHours(6.5), true, false, Played.AddHours(7.5)));
        // After the window: no more until the next play.
        Assert.False(ArmoryPlanner.Due(Karuzo, Played.AddHours(20), true, false, Played.AddHours(25)));
        // Never played, as far as the roster says: the sweep only.
        Assert.False(ArmoryPlanner.Due(Karuzo with { Activity = null }, null, true, false, Played));
        // A hold that ended: once, whatever the window.
        Assert.True(ArmoryPlanner.Due(Karuzo, Played.AddHours(30), true, heldBefore: true, Played.AddDays(10)));
    }

    internal static string Db(params (string Guid, string Name, int Level, long Updated, long GearAt)[] chars) =>
        "AltStableDB = {\n" + string.Concat(chars.Select(c =>
            $"[\"{c.Guid}\"] = {{\n[\"name\"] = \"{c.Name}\",\n[\"level\"] = {c.Level},\n[\"lastUpdate\"] = {c.Updated},\n[\"gearAt\"] = {c.GearAt},\n}},\n")) + "}\n";

    internal static string Hidden(params string[] guids) =>
        "AltStableConfig = {\n[\"hiddenCharacters\"] = {\n" + string.Concat(guids.Select(g => $"[\"{g}\"] = true,\n")) + "},\n}\n";

    [Fact]
    public void Eligible_is_level_ten_on_any_account_hidden_on_none_with_the_latest_play()
    {
        using var t = new TempInstall();
        t.WriteStore("1#1", Db(("Player-1-0000AAAA", "Old Name", 9, 1000, 1500), ("Player-1-0000BBBB", "Hidden One", 30, 1, 1)) + Hidden());
        t.WriteStore("1#2", Db(("Player-1-0000AAAA", "New Name", 10, 2000, 0), ("Player-1-0000CCCC", "Low", 9, 1, 1)) + Hidden("Player-1-0000BBBB"));
        var (chars, refused) = ArmoryEligibility.Select(SavedVariablesReader.Snapshot(t.Install.AccountsDir));
        Assert.Null(refused);
        var c = Assert.Single(chars);
        Assert.Equal(("Player-1-0000AAAA", "New Name", 10), (c.Guid, c.Name, c.Level));
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(2000), c.Activity);
    }

    [Fact]
    public void A_roster_that_cannot_be_read_asks_about_nobody()
    {
        using var t = new TempInstall();
        t.WriteStore("1#1", Db(("Player-1-0000AAAA", "A", 20, 1, 1)));
        t.WriteStore("1#2", "AltStableDB = {\n[\"x\"] = {\n");
        var (chars, refused) = ArmoryEligibility.Select(SavedVariablesReader.Snapshot(t.Install.AccountsDir));
        Assert.Empty(chars);
        Assert.NotNull(refused);
    }

    private static AttemptHistory History(string dir, string guid, params (string? Source, DateTime Started)[] attempts)
    {
        var h = AttemptHistory.Load(dir, guid);
        var n = 0;
        foreach (var (source, started) in attempts)
        {
            var sig = "sig" + n++;
            h.Begin(new Attempt { Signature = sig, Started = started, Source = source });
            h.End(sig, Attempt.Failed + ": test", started.AddMinutes(1));
        }
        return h;
    }

    [Fact]
    public void The_hold_counts_from_the_last_armory_attempt_whatever_came_of_it()
    {
        using var t = new TempInstall();
        var dir = Path.Combine(t.Root, "enhanced");
        var t0 = new DateTime(2026, 11, 1, 12, 0, 0);
        // A legacy history (no source) is all captures: no hold.
        Assert.Null(Armory.HeldUntil(History(dir, "Player-1-0000AAAA", (null, t0)), 7, t0.AddHours(1)));
        // An armory attempt, then a capture's: the capture lifts nothing.
        var h = History(dir, "Player-1-0000BBBB", (Attempt.FromArmory, t0), (Attempt.FromCapture, t0.AddDays(1)));
        Assert.Equal(t0.AddDays(7), Armory.HeldUntil(h, 7, t0.AddDays(2)));
        Assert.Null(Armory.HeldUntil(h, 7, t0.AddDays(7)));
        Assert.Equal(t0.AddDays(1), Armory.HeldUntil(h, 0, t0.AddHours(1)));   // no 0: at least a day
    }

    [Fact]
    public void A_settings_file_from_before_the_question_has_not_answered_it()
    {
        using var t = new TempInstall();
        var dir = Path.Combine(t.Root, "data");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "settings.json"), "{ \"Started\": true, \"WowFlavorDir\": \"C:\\\\x\" }");
        var s = Settings.Load(dir);
        Assert.Null(s.Armory);
        Assert.False(s.ArmorySd);
        Assert.Equal(7, s.ArmoryRemakeDays);
        var answered = s with { Armory = false, ArmoryRemakeDays = 500 };
        Assert.Equal(90, answered.ArmoryRemakeDays);
        answered.Save(dir);
        Assert.Equal(answered, Settings.Load(dir));
        Assert.NotEqual(s, answered with { ArmoryRemakeDays = 7 });           // false is an answer
        Assert.NotEqual(s with { Armory = true }, s with { Armory = true, ArmorySd = true });
    }
}

/// <summary>What a row says about the armory.</summary>
public class ArmoryTextTests
{
    private static readonly DateTime Today = new(2026, 11, 11, 15, 0, 0);

    [Fact]
    public void A_row_says_where_its_picture_came_from_and_what_the_armory_said()
    {
        var render = new PortraitRow("Karuzo Macphisto", "Player-1-0000AAAA", "karuzo.tga", PortraitSource.File, Today.AddHours(-2),
            null, CaptureOutcome.None, null, Size: (130, 700), Folder: "Armory");
        Assert.Equal("Armory picture, today 13:00", PassText.RowSummary(render, Today));
        Assert.Equal("armory picture · fetched today 13:00", PassText.RowDetail(render, Today));
        Assert.Equal("Ready", PassText.RowState(render));

        // The hold, with its time: today and tomorrow by name, any other day by its date; over any other note.
        var held = render with { ArmoryHeldUntil = Today.AddHours(4).AddMinutes(29), ArmoryNote = "no armory render yet" };
        Assert.Equal("Armory picture, today 13:00 · armory updates resume today 19:29", PassText.RowSummary(held, Today));
        Assert.EndsWith("armory updates resume today 19:29", PassText.RowDetail(held, Today));
        Assert.EndsWith("armory updates resume tomorrow 19:29", PassText.RowSummary(held with { ArmoryHeldUntil = Today.AddDays(1).AddHours(4).AddMinutes(29) }, Today));
        Assert.EndsWith("armory updates resume 2026-11-18 19:29", PassText.RowSummary(held with { ArmoryHeldUntil = Today.AddDays(7).AddHours(4).AddMinutes(29) }, Today));

        // A capture, converted, and a later armory picture showing.
        var both = new PortraitRow("Karuzo Macphisto", "Player-1-0000AAAA", "karuzo.tga", PortraitSource.ByGuid, Today.AddHours(-1),
            Today.AddDays(-2), CaptureOutcome.Converted, null, Size: (130, 700), Folder: "Armory");
        Assert.Contains("converted · showing the armory picture, fetched today 14:00", PassText.RowDetail(both, Today));
        // Its enhanced picture is of the capture, and not drawn: said so, not "enhanced".
        Assert.EndsWith("armory picture not enhanced yet", PassText.RowDetail(both with { EnhanceNote = "enhanced (realistic)" }, Today));
        Assert.EndsWith("enhanced (realistic)", PassText.RowDetail(both with { EnhanceNote = "enhanced (realistic)", Enhanced = new EnhancedTexture(1, 2, 4, 4) }, Today));

        // A character the armory knows, with nothing yet: a row, not a problem, and not a portrait.
        var none = new PortraitRow("Morphisto", "Player-1-0000BBBB", null, PortraitSource.None, null, null, CaptureOutcome.None, null,
            ArmoryNote: "no armory render yet");
        Assert.Equal("No armory render yet", PassText.RowSummary(none, Today));
        Assert.Equal("no capture · no armory render yet", PassText.RowDetail(none, Today));
        Assert.False(none.NeedsAttention);
        Assert.Equal("1", PassText.Count([render, none]));
    }
}

/// <summary>Captures and renders side by side on disk: which one the manifest names, and whose name is whose.</summary>
public class ArmoryFolderTests
{
    private const string A = "Player-1-0000AAAA";
    private const string B = "Player-1-0000BBBB";
    private static readonly DateTimeOffset Made = new(2026, 11, 11, 11, 40, 0, TimeSpan.Zero);

    internal static byte[] Tga(int w, int h, byte shade)
    {
        var img = TestData.Solid(w, h, shade, 0, 0);
        var ms = new MemoryStream();
        TgaCodec.Write(ms, img);
        return ms.ToArray();
    }

    private static void Capture(CutoutFolder folder, string fileBase, string guid, DateTimeOffset at) =>
        folder.WriteCutout(fileBase, TestData.Solid(4, 8, 1, 2, 3), new CutoutMeta
        {
            W = 4, H = 8, TexW = 4, TexH = 8, Guid = guid, Epoch = at.ToUnixTimeSeconds(),
        });

    private static void Render(CutoutFolder folder, string fileBase, string guid, DateTimeOffset made, byte shade = 9) =>
        folder.WriteArmory(fileBase, Tga(4, 8, shade), new CutoutMeta
        {
            W = 4, H = 8, TexW = 4, TexH = 8, Guid = guid, Url = Armory.Url(guid, false), LastModified = made.ToString("R"),
        });

    private static void Us(TempInstall t)
    {
        Directory.CreateDirectory(Path.Combine(t.Install.FlavorDir, "WTF"));
        File.WriteAllText(Path.Combine(t.Install.FlavorDir, "WTF", "Config.wtf"), "SET portal \"US\"\n");
    }

    [Fact]
    public void Armory_true_is_on_each_character_the_armory_has_drawn_whichever_file_its_entry_names()
    {
        using var t = new TempInstall();
        const string C = "Player-1-0000CCCC", D = "Player-1-0000DDDD", E = "Player-1-0000EEEE";
        t.WriteStore("1#1", ArmoryRuleTests.Db((A, "A", 20, 1, 1), (B, "B", 20, 1, 1), (C, "C", 20, 1, 1), (D, "D", 9, 1, 1), (E, "E", 20, 1, 1))
            + ArmoryRuleTests.Hidden(C) + "AltStablePortraits = nil\n");
        Us(t);
        var folder = new CutoutFolder(t.Install.CutoutAddonDir);
        Render(folder, "a", A, Made);                                 // the render is the portrait
        Render(folder, "b", B, Made);
        Capture(folder, "b", B, Made);                                // the capture is, and the armory draws B too
        Render(folder, "c", C, Made);                                 // hidden
        Render(folder, "d", D, Made);                                 // below level 10
        Capture(folder, "e", E, Made);                                // never drawn: the prompt stays
        IReadOnlyDictionary<string, bool> Kept() =>
            Armory.Flag(folder.Inventory(PortraitJudge.Unknown), folder, t.Install).ToDictionary(e => e.Guid!, e => e.KeptByArmory);

        Assert.Equal(new Dictionary<string, bool> { [A] = true, [B] = true, [C] = false, [D] = false, [E] = false }, Kept());
        Assert.Null(Assert.Single(folder.Inventory(PortraitJudge.Unknown), e => e.Guid == B).Folder);

        var lua = ManifestWriter.Render(Armory.Flag(folder.Inventory(PortraitJudge.Unknown), folder, t.Install), DateTime.Now);
        Assert.Contains(@"Cutouts\Armory\a.tga]], w = 4, h = 8, texw = 4, texh = 8, armory = true },", lua);
        Assert.Contains(@"Cutouts\b.tga]], w = 4, h = 8, texw = 4, texh = 8, armory = true },", lua);
        Assert.DoesNotContain(@"e.tga]], w = 4, h = 8, texw = 4, texh = 8, armory", lua);

        // A roster that cannot be read, or another region: nobody, and the addon asks as before.
        t.WriteStore("1#2", "AltStableDB = {\n[\"x\"] = {\n");
        Assert.DoesNotContain(true, Kept().Values);
        File.Delete(Path.Combine(t.Install.AccountsDir, "1#2", "SavedVariables", "AltStable.lua"));
        Assert.True(Kept()[A]);
        File.WriteAllText(Path.Combine(t.Install.FlavorDir, "WTF", "Config.wtf"), "SET portal \"EU\"\n");
        Assert.DoesNotContain(true, Kept().Values);
    }

    [Fact]
    public void A_roster_time_no_date_can_hold_is_no_time_and_the_capture_s_manifest_is_still_written()
    {
        using var t = new TempInstall();
        var (black, white) = TestData.Pair(400, 1200, 150, 250, 100, 700);
        var at = new DateTime(2026, 11, 11, 20, 0, 0);
        t.WriteShot(at, black);
        t.WriteShot(at.AddSeconds(1), white);
        t.WriteStore("1#1", TestData.SavedVariables([
            TestData.Record("Kaleid Sumner", B, 1, at), TestData.Record("Kaleid Sumner", B, 2, at.AddSeconds(1))]));
        // Another account's roster says times no date can hold, beside real ones.
        t.WriteStore("1#2", ArmoryRuleTests.Db((A, "A", 20, 999999999999, 1794370273), (B, "Kaleid Sumner", 20, 1794370000, long.MaxValue))
            + ArmoryRuleTests.Hidden() + "AltStablePortraits = nil\n");
        Us(t);
        var folder = new CutoutFolder(t.Install.CutoutAddonDir);
        Render(folder, "a", A, Made);

        var report = new ConvertPass(t.Install, new ConvertOptions(Armory: true)).Run();

        Assert.Equal(["Kaleid Sumner"], report.Written);
        Assert.True(report.ManifestWritten);
        Assert.Contains(@"Cutouts\Armory\a.tga]], w = 4, h = 8, texw = 4, texh = 8, armory = true },", File.ReadAllText(folder.ManifestPath));
        var (characters, _) = ArmoryEligibility.Select(SavedVariablesReader.Snapshot(t.Install.AccountsDir));
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1794370273), characters.Single(c => c.Guid == A).Activity);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1794370000), characters.Single(c => c.Guid == B).Activity);
    }

    [Fact]
    public void A_render_alone_is_listed_from_the_armory_folder()
    {
        using var t = new TempInstall();
        var folder = new CutoutFolder(t.Install.CutoutAddonDir);
        Render(folder, "karuzo", A, Made);
        var entry = Assert.Single(folder.Inventory(PortraitJudge.Unknown));
        Assert.Equal((A, "karuzo.tga", "Armory"), (entry.Key, entry.FileName, entry.Folder));
        Assert.Null(entry.NativeH);
        folder.WriteManifest(PortraitJudge.Unknown, DateTime.Now);
        Assert.Contains(@"file = [[Interface\AddOns\AltStableCutouts\Cutouts\Armory\karuzo.tga]]", File.ReadAllText(folder.ManifestPath));
    }

    [Fact]
    public void The_newer_of_capture_and_render_is_named_by_the_seven_hour_estimate()
    {
        using var t = new TempInstall();
        var folder = new CutoutFolder(t.Install.CutoutAddonDir);
        Render(folder, "karuzo", A, Made);
        // Captured during the session the render shows (it ended about 04:40): the render.
        Capture(folder, "karuzo", A, Made.AddHours(-8));
        Assert.Equal("Armory", Assert.Single(folder.Inventory(PortraitJudge.Unknown)).Folder);
        Assert.True(folder.Primaries(PortraitJudge.Unknown)[A].Armory);
        // Captured after it: the capture, with its own size and native height.
        Capture(folder, "karuzo", A, Made.AddHours(-7));
        var entry = Assert.Single(folder.Inventory(PortraitJudge.Unknown));
        Assert.Null(entry.Folder);
        Assert.False(folder.Primaries(PortraitJudge.Unknown)[A].Armory);
    }

    [Fact]
    public void A_render_whose_tga_is_not_the_one_its_sidecar_names_is_not_used()
    {
        using var t = new TempInstall();
        var folder = new CutoutFolder(t.Install.CutoutAddonDir);
        Render(folder, "karuzo", A, Made);
        Capture(folder, "karuzo", A, Made.AddDays(-30));
        // The app stopped between the TGA and its sidecar.
        File.WriteAllBytes(Path.Combine(folder.ArmoryDir, "karuzo.tga"), Tga(4, 8, 77));
        Assert.Empty(folder.ArmoryRenders());
        Assert.Null(Assert.Single(folder.Inventory(PortraitJudge.Unknown)).Folder);      // the capture, old as it is
        Assert.Equal("karuzo", folder.ArmoryBaseOf(A));               // and the name is kept for the refetch
    }

    [Fact]
    public void Two_namesakes_with_renders_only_do_not_share_a_file()
    {
        using var t = new TempInstall();
        var folder = new CutoutFolder(t.Install.CutoutAddonDir);
        var first = folder.BaseFor(A, "Twin Name");
        Render(folder, first, A, Made);
        var second = folder.BaseFor(B, "Twin Name");
        Assert.Equal(("twin-name", "twin-name-00bbbb"), (first, second));
        Render(folder, second, B, Made);
        Assert.Equal(2, folder.Inventory(PortraitJudge.Unknown).Count);
        // Fetched again: the same names.
        Assert.Equal(("twin-name", "twin-name-00bbbb"), (folder.BaseFor(A, "Twin Name"), folder.BaseFor(B, "Twin Name")));
    }

    [Fact]
    public void A_first_capture_takes_its_render_s_name_and_never_another_character_s()
    {
        using var t = new TempInstall();
        var (black, white) = TestData.Pair(400, 1200, 150, 250, 100, 700);
        var at = new DateTime(2026, 11, 11, 20, 0, 0);
        t.WriteShot(at, black);
        t.WriteShot(at.AddSeconds(1), white);
        t.WriteStore("1#1", TestData.SavedVariables([
            TestData.Record("Twin Name", B, 1, at), TestData.Record("Twin Name", B, 2, at.AddSeconds(1))]));
        var folder = new CutoutFolder(t.Install.CutoutAddonDir);
        // A has the render named twin-name; B is captured for the first time.
        Render(folder, "twin-name", A, Made);
        new ConvertPass(t.Install, new ConvertOptions(KeepScreenshots: true)).Run();
        Assert.Equal("twin-name-00bbbb", folder.FileBaseOf(B));
        Assert.Equal(A, folder.ReadArmoryMeta("twin-name")!.Guid);

        // And a character whose render came first keeps that name for its capture.
        using var u = new TempInstall();
        u.WriteShot(at, black);
        u.WriteShot(at.AddSeconds(1), white);
        u.WriteStore("1#1", TestData.SavedVariables([
            TestData.Record("Kaleid Sumner", A, 1, at), TestData.Record("Kaleid Sumner", A, 2, at.AddSeconds(1))]));
        var other = new CutoutFolder(u.Install.CutoutAddonDir);
        Render(other, "an-older-name", A, Made);
        new ConvertPass(u.Install, new ConvertOptions(KeepScreenshots: true)).Run();
        Assert.Equal("an-older-name", other.FileBaseOf(A));
        // The capture is the newer: it is listed, from Cutouts\.
        Assert.Null(Assert.Single(other.Inventory(PortraitJudge.Unknown)).Folder);
    }

    [Fact]
    public void A_render_s_enhanced_picture_is_found_by_its_base_and_its_own_bytes()
    {
        using var t = new TempInstall();
        var folder = new CutoutFolder(t.Install.CutoutAddonDir);
        Render(folder, "karuzo", A, Made);
        var primary = Path.Combine(folder.ArmoryDir, "karuzo.tga");
        var picture = TestData.Solid(4, 8, 200, 0, 0);
        var ms = new MemoryStream();
        TgaCodec.Write(ms, picture);
        folder.WriteEnhanced("karuzo", picture, new CutoutMeta
        {
            W = 4, H = 8, TexW = 4, TexH = 8, Guid = A,
            Enhancement = new EnhancementMeta { SourceHash = EnhancementSignature.HashOf(primary), OutputHash = EnhancementSignature.HashOf(ms.ToArray()) },
        });
        var entry = Assert.Single(folder.Inventory(PortraitJudge.Unknown));
        Assert.NotNull(entry.Enhanced);
        folder.WriteManifest(PortraitJudge.Unknown, DateTime.Now);
        var lua = File.ReadAllText(folder.ManifestPath);
        Assert.Contains(@"Cutouts\Armory\karuzo.tga]]", lua);
        Assert.Contains(@"Cutouts\Enhanced\karuzo.tga]]", lua);
        var row = Collection.Build(folder.Inventory(PortraitJudge.Unknown), [], folder.FileTimes(folder.Inventory(PortraitJudge.Unknown))).Single();
        Assert.Equal(Path.Combine("Enhanced", "karuzo.tga"), row.ThumbnailFile);
        Assert.True(row.FromArmory);
        Assert.NotNull(row.FileModified);
    }
}

/// <summary>A CDN that answers what the test says, and remembers what was asked.</summary>
internal sealed class FakeCdn : HttpMessageHandler
{
    public readonly List<(string Url, string? IfNoneMatch, DateTimeOffset? IfModifiedSince, string? Agent)> Requests = [];
    /// <summary>Each request's Authorization header, beside <see cref="Requests"/>.</summary>
    public readonly List<string?> Auth = [];
    public Func<HttpRequestMessage, HttpResponseMessage> Respond = _ => new HttpResponseMessage(HttpStatusCode.Forbidden);

    public int Count
    {
        get { lock (Requests) return Requests.Count; }
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        lock (Requests)
        {
            Requests.Add((request.RequestUri!.ToString(), request.Headers.IfNoneMatch.FirstOrDefault()?.ToString(),
                request.Headers.IfModifiedSince, request.Headers.UserAgent.ToString()));
            Auth.Add(request.Headers.Authorization?.ToString());
        }
        return Task.FromResult(Respond(request));
    }

    public static HttpResponseMessage Png(byte[] body, string etag, DateTimeOffset made)
    {
        var r = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };
        r.Headers.ETag = new EntityTagHeaderValue(etag);
        r.Content.Headers.LastModified = made;
        r.Headers.Date = DateTimeOffset.UtcNow;
        return r;
    }

    /// <summary>A render: a transparent canvas with a standing figure of this colour.</summary>
    public static byte[] RenderPng(byte shade, int h = 700)
    {
        var img = new RgbaImage(400, 800);
        for (var y = 50; y < 50 + h; y++)
            for (var x = 100; x < 230; x++)
                img[x, y] = (shade, 40, 80, 255);
        return PngCodec.Write(img);
    }
}

/// <summary>A body whose length is not known until it ends: a stream that cannot seek.</summary>
internal sealed class Unsized(long length) : Stream
{
    private long _left = length;
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count)
    {
        var n = (int)Math.Min(count, _left);
        _left -= n;
        return n;
    }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

public class ArmoryClientTests
{
    private const string Url = "https://render.worldofwarcraft.com/forever-us/character/138/678282-main-raw.png";

    [Fact]
    public async Task A_render_comes_back_with_its_validators_and_they_are_sent_next_time()
    {
        var cdn = new FakeCdn();
        var made = new DateTimeOffset(2026, 11, 11, 11, 40, 17, TimeSpan.Zero);
        cdn.Respond = _ => FakeCdn.Png([1, 2, 3], "\"abc\"", made);
        var client = new ArmoryClient(() => cdn, "1.2.0+deadbeef");
        var got = await client.FetchAsync(Url, null, null, CancellationToken.None);
        Assert.Equal(ArmoryStatus.Ok, got.Status);
        Assert.Equal([1, 2, 3], got.Body);
        Assert.Equal("\"abc\"", got.ETag);
        Assert.Equal("Wed, 11 Nov 2026 11:40:17 GMT", got.LastModified);
        Assert.Null(cdn.Requests[0].IfNoneMatch);
        Assert.Equal("AltStableCompanion/1.2.0+deadbeef", cdn.Requests[0].Agent);

        cdn.Respond = _ => new HttpResponseMessage(HttpStatusCode.NotModified);
        got = await client.FetchAsync(Url, got.ETag, got.LastModified, CancellationToken.None);
        Assert.Equal(ArmoryStatus.NotModified, got.Status);
        Assert.Equal("\"abc\"", cdn.Requests[1].IfNoneMatch);
        Assert.Equal(made, cdn.Requests[1].IfModifiedSince);
    }

    [Theory]
    [InlineData(403, ArmoryStatus.NoRender)]
    [InlineData(404, ArmoryStatus.NoRender)]
    [InlineData(429, ArmoryStatus.Busy)]
    [InlineData(503, ArmoryStatus.Busy)]
    [InlineData(307, ArmoryStatus.Failed)]
    [InlineData(400, ArmoryStatus.Failed)]
    public async Task Each_answer_means_one_thing(int code, ArmoryStatus status)
    {
        var cdn = new FakeCdn { Respond = _ => new HttpResponseMessage((HttpStatusCode)code) };
        var got = await new ArmoryClient(() => cdn, "1").FetchAsync(Url, null, null, CancellationToken.None);
        Assert.Equal(status, got.Status);
        if (status == ArmoryStatus.Busy) Assert.Equal(ArmoryClient.DefaultBusy, got.RetryAfter);
    }

    [Fact]
    public async Task Retry_after_is_obeyed_and_a_render_too_big_or_elsewhere_is_not_taken()
    {
        var cdn = new FakeCdn();
        cdn.Respond = _ =>
        {
            var r = new HttpResponseMessage((HttpStatusCode)429);
            r.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMinutes(5));
            return r;
        };
        var client = new ArmoryClient(() => cdn, "1");
        Assert.Equal(TimeSpan.FromMinutes(5), (await client.FetchAsync(Url, null, null, CancellationToken.None)).RetryAfter);

        // Too big, said up front; and too big, found while reading a body that did not say.
        cdn.Respond = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new MemoryStream(new byte[ArmoryClient.MaxBytes + 1])) };
        var big = await client.FetchAsync(Url, null, null, CancellationToken.None);
        Assert.Equal(ArmoryStatus.Failed, big.Status);
        Assert.Null(big.Body);
        cdn.Respond = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new Unsized(ArmoryClient.MaxBytes + 1)) };
        Assert.Equal(ArmoryStatus.Failed, (await client.FetchAsync(Url, null, null, CancellationToken.None)).Status);
        cdn.Respond = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new Unsized(1000)) };
        Assert.Equal(1000, (await client.FetchAsync(Url, null, null, CancellationToken.None)).Body!.Length);

        var before = cdn.Count;
        Assert.Equal(ArmoryStatus.Failed, (await client.FetchAsync("https://example.com/x.png", null, null, CancellationToken.None)).Status);
        Assert.Equal(ArmoryStatus.Failed, (await client.FetchAsync("http://render.worldofwarcraft.com/x.png", null, null, CancellationToken.None)).Status);
        Assert.Equal(before, cdn.Count);                            // nothing was sent
    }
}

/// <summary>The worker, run for real against a fake CDN and a temporary install.</summary>
public class ArmoryWorkerTests
{
    private const string A = "Player-6796-000A598A";
    private const string AUrl = "https://render.worldofwarcraft.com/forever-us/character/138/678282-main-raw.png";
    private static readonly DateTimeOffset Made = DateTimeOffset.UtcNow.AddHours(-1);

    internal static void Roster(TempInstall t, string guid = A, string name = "Karuzo Macphisto", int level = 25, bool hidden = false, string portal = "US",
        long updated = 1794370273, (string Guid, string Name, bool Hidden)? also = null)
    {
        static string Record(string guid, string name, int level, long updated) =>
            "[\"" + guid + "\"] = {\n[\"name\"] = \"" + name + "\",\n[\"class\"] = \"WARLOCK\",\n[\"race\"] = \"Troll\",\n[\"gender\"] = \"Female\",\n[\"level\"] = "
            + level + ",\n[\"lastUpdate\"] = " + updated + ",\n},\n";
        var hide = (hidden ? $"[\"{guid}\"] = true,\n" : "") + (also is { Hidden: true } h ? $"[\"{h.Guid}\"] = true,\n" : "");
        var records = Record(guid, name, level, updated) + (also is { } b ? Record(b.Guid, b.Name, level, updated) : "");
        t.WriteStore("1#1", "AltStableDB = {\n" + records + "}\nAltStableConfig = {\n[\"hiddenCharacters\"] = {\n" + hide + "},\n}\nAltStablePortraits = nil\n");
        Directory.CreateDirectory(Path.Combine(t.Install.FlavorDir, "WTF"));
        File.WriteAllText(Path.Combine(t.Install.FlavorDir, "WTF", "Config.wtf"), $"SET portal \"{portal}\"\n");
    }

    internal static string Data(TempInstall t) => Path.Combine(t.Root, "data");

    internal static Controller Started(TempInstall t, FakeCdn cdn, bool? armory = true, bool enhance = false, EnhanceHooks? hooks = null, int days = 7,
        Func<DateTime>? clock = null, bool paused = false)
    {
        new Settings { Started = true, WowFlavorDir = t.Install.FlavorDir, Armory = armory, Enhance = enhance, ArmoryRemakeDays = days, Paused = paused }.Save(Data(t));
        var c = new Controller(new StartupOptions(DataDir: Data(t)), () => throw new InvalidOperationException("detection was used"),
            enhance: hooks, armoryHttp: () => cdn, clock: clock);
        c.Start();
        return c;
    }

    internal static void Until(Func<bool> happened, string what, int seconds = 15)
    {
        var limit = DateTime.UtcNow.AddSeconds(seconds);
        while (!happened())
        {
            Assert.True(DateTime.UtcNow < limit, "never happened: " + what);
            Thread.Sleep(50);
        }
    }

    [Fact]
    public void A_render_becomes_the_portrait_of_a_character_never_captured()
    {
        using var t = new TempInstall();
        Roster(t);
        var cdn = new FakeCdn { Respond = _ => FakeCdn.Png(FakeCdn.RenderPng(150), "\"v1\"", Made) };
        using var c = Started(t, cdn);
        var folder = new CutoutFolder(t.Install.CutoutAddonDir);
        Until(() => File.Exists(folder.ManifestPath) && File.ReadAllText(folder.ManifestPath).Contains(@"Armory\karuzo-macphisto.tga"), "the render in the manifest");
        Assert.Equal(AUrl, cdn.Requests[0].Url);
        var meta = folder.ReadArmoryMeta("karuzo-macphisto")!;
        Assert.Equal((A, AUrl, "\"v1\""), (meta.Guid, meta.Url, meta.ETag));
        Assert.Equal(EnhancementSignature.HashOf(Path.Combine(folder.ArmoryDir, "karuzo-macphisto.tga")), meta.TgaHash);
        Assert.Equal((130, 700), (meta.W, meta.H));
        Assert.True(File.Exists(folder.TocPath));
        Until(() => c.Current.Shell.Report?.Portraits?.Any(r => r.FromArmory) == true, "the row");
        var row = c.Current.Shell.Report!.Portraits!.Single(r => r.Guid == A);
        Assert.Equal("Karuzo Macphisto", row.Name);                  // the roster's name, not the file's
    }

    [Fact]
    public void Checked_again_the_validators_go_with_it_and_the_same_pixels_keep_the_file()
    {
        using var t = new TempInstall();
        Roster(t);
        var cdn = new FakeCdn { Respond = _ => FakeCdn.Png(FakeCdn.RenderPng(150), "\"v1\"", Made) };
        using var c = Started(t, cdn);
        var folder = new CutoutFolder(t.Install.CutoutAddonDir);
        var tga = Path.Combine(folder.ArmoryDir, "karuzo-macphisto.tga");
        Until(() => folder.ArmoryRenders().ContainsKey(A), "the render");
        var written = File.GetLastWriteTimeUtc(tga);

        cdn.Respond = _ => new HttpResponseMessage(HttpStatusCode.NotModified);
        c.CheckArmoryNow();
        Until(() => cdn.Count == 2, "the second look");
        Assert.Equal("\"v1\"", cdn.Requests[1].IfNoneMatch);

        // Rendered again, the same picture: a new date on the sidecar, the TGA untouched.
        var later = Made.AddHours(1);
        cdn.Respond = _ => FakeCdn.Png(FakeCdn.RenderPng(150), "\"v2\"", later);
        c.CheckArmoryNow();
        Until(() => folder.ReadArmoryMeta("karuzo-macphisto")?.ETag == "\"v2\"", "the new validators");
        Assert.Equal(written, File.GetLastWriteTimeUtc(tga));
        Assert.Equal(later.ToString("R"), folder.ReadArmoryMeta("karuzo-macphisto")!.LastModified);

        // Another picture: a new TGA.
        cdn.Respond = _ => FakeCdn.Png(FakeCdn.RenderPng(60), "\"v3\"", later.AddHours(1));
        c.CheckArmoryNow();
        Until(() => folder.ReadArmoryMeta("karuzo-macphisto")?.ETag == "\"v3\"", "the new render");
        Assert.Equal(EnhancementSignature.HashOf(tga), folder.ReadArmoryMeta("karuzo-macphisto")!.TgaHash);
    }

    [Theory]
    [InlineData(null, false, 25, "US")]          // not answered
    [InlineData(false, false, 25, "US")]         // answered no
    [InlineData(true, true, 25, "US")]           // hidden
    [InlineData(true, false, 9, "US")]           // below the armory's levels
    [InlineData(true, false, 25, "EU")]          // a region not measured
    public void Nothing_is_asked_unless_the_player_said_yes_for_a_character_the_armory_has(bool? armory, bool hidden, int level, string portal)
    {
        using var t = new TempInstall();
        Roster(t, level: level, hidden: hidden, portal: portal);
        var cdn = new FakeCdn();
        using var c = Started(t, cdn, armory);
        Until(() => c.Current.Shell.Report is not null, "the first pass");
        Thread.Sleep(1500);
        Assert.Equal(0, cdn.Count);
    }

    [Fact]
    public void Turned_on_it_asks_at_once_and_a_render_that_does_not_exist_says_so()
    {
        using var t = new TempInstall();
        Roster(t);
        var cdn = new FakeCdn();
        using var c = Started(t, cdn, armory: null);
        Until(() => c.Current.Shell.Report is not null, "the first pass");
        c.SetArmory(true);
        Until(() => c.Current.Shell.Report?.Portraits?.Any(r => r.ArmoryNote == "no armory render yet") == true, "the row's note");
        Assert.Equal(1, cdn.Count);
        Assert.True(Settings.Load(Data(t)).Armory);
    }

    private const string B = "Player-6796-00381101";
    private static long DaysAgo(int days) => DateTimeOffset.UtcNow.AddDays(-days).ToUnixTimeSeconds();

    [Theory]
    [InlineData(true)]       // the second character hidden while the first was asked
    [InlineData(false)]      // the account no longer readable
    public void Who_may_be_asked_about_is_read_again_before_each_request(bool hide)
    {
        using var t = new TempInstall();
        Roster(t, also: (B, "Zoruka Sumner", false));
        using var held = new ManualResetEventSlim();
        var cdn = new FakeCdn
        {
            Respond = r =>
            {
                if (r.RequestUri!.ToString() == AUrl) held.Wait(TimeSpan.FromSeconds(10));
                return new HttpResponseMessage(HttpStatusCode.Forbidden);
            },
        };
        using var c = Started(t, cdn);
        Until(() => cdn.Count == 1, "Karuzo asked first");
        if (hide) Roster(t, also: (B, "Zoruka Sumner", true));
        else t.WriteStore("1#1", "AltStableDB = {\n[\"x\"] = {\n");
        held.Set();
        Thread.Sleep(2500);                                           // the gap, and more
        lock (cdn.Requests) Assert.Equal([AUrl], cdn.Requests.Select(q => q.Url));
    }

    [Fact]
    public void Hd_or_sd_changed_while_a_request_is_out_its_answer_is_dropped_and_the_new_one_asked()
    {
        using var t = new TempInstall();
        Roster(t, updated: DaysAgo(3));                               // outside the hours after play
        using var held = new ManualResetEventSlim();
        var cdn = new FakeCdn
        {
            Respond = r =>
            {
                if (r.RequestUri!.ToString() == AUrl) held.Wait(TimeSpan.FromSeconds(10));
                return FakeCdn.Png(FakeCdn.RenderPng(150), "\"v1\"", Made);
            },
        };
        using var c = Started(t, cdn);
        Until(() => cdn.Count == 1, "the HD request");
        c.SetArmorySd(true);
        held.Set();
        var folder = new CutoutFolder(t.Install.CutoutAddonDir);
        Until(() => folder.ReadArmoryMeta("karuzo-macphisto")?.Url == Armory.Url(A, true), "the SD render");
        lock (cdn.Requests) Assert.Equal([AUrl, Armory.Url(A, true)], cdn.Requests.Select(q => q.Url));
    }

    [Fact]
    public void A_character_told_to_wait_is_asked_again_once_the_wait_is_over()
    {
        using var t = new TempInstall();
        Roster(t, updated: DaysAgo(3));                               // outside the hours after play
        var shift = TimeSpan.Zero;
        var cdn = new FakeCdn { Respond = _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) };
        using var c = Started(t, cdn, clock: () => DateTime.Now + shift);
        Until(() => c.Current.Shell.Report?.Portraits?.Any(r => r.ArmoryNote == "the armory asked to wait") == true, "the wait");
        cdn.Respond = _ => FakeCdn.Png(FakeCdn.RenderPng(150), "\"v1\"", Made);
        shift = ArmoryClient.DefaultBusy + TimeSpan.FromMinutes(1);
        c.SetArmoryRemakeDays(8);                                     // wakes the worker, as its minute's tick would
        var folder = new CutoutFolder(t.Install.CutoutAddonDir);
        Until(() => folder.ArmoryRenders().ContainsKey(A), "the render after the wait");
        Assert.Equal(2, cdn.Count);
    }

    [Fact]
    public void The_manifest_says_armory_while_the_answer_is_yes_and_not_after_a_no()
    {
        using var t = new TempInstall();
        Roster(t);
        var folder = new CutoutFolder(t.Install.CutoutAddonDir);
        folder.WriteArmory("karuzo-macphisto", ArmoryFolderTests.Tga(4, 8, 9), new CutoutMeta
        {
            W = 4, H = 8, TexW = 4, TexH = 8, Guid = A, Url = AUrl, ETag = "\"v1\"", LastModified = Made.ToString("R"),
        });
        var cdn = new FakeCdn { Respond = _ => new HttpResponseMessage(HttpStatusCode.NotModified) };
        string Manifest() => File.Exists(folder.ManifestPath) ? File.ReadAllText(folder.ManifestPath) : "";
        using var c = Started(t, cdn);
        Until(() => Manifest().Contains("armory = true"), "the flag from the first pass");
        c.SetArmory(false);
        Until(() => Manifest().Contains(@"Armory\karuzo-macphisto.tga") && !Manifest().Contains("armory = true"), "the flag gone");
        c.ConvertNow();                                               // a pass, with the answer it was given
        Thread.Sleep(1500);
        Assert.DoesNotContain("armory = true", Manifest());
        c.SetArmory(true);
        Until(() => Manifest().Contains("armory = true"), "the flag back");
        c.ConvertNow();
        Thread.Sleep(1500);
        Assert.Contains("armory = true", Manifest());
    }
}
