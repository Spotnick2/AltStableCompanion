using AltStableCompanion.Core;
using Xunit;

namespace AltStableCompanion.Core.Tests;

/// <summary>Which of capture and render is the portrait (contract section 4, amended by AltStable#310).</summary>
public class PortraitJudgeTests
{
    private const string A = "Player-6796-000A598A";
    private const long E = 1_794_400_000;                                  // the capture's epoch
    private const string Look = "1:100;3:0;4:7;5:300;15:400;16:500;19:9";   // the shirt and tabard too, as the addon writes it
    // The render shows the character as of 3 hours after the capture.
    private static readonly DateTimeOffset Made = DateTimeOffset.FromUnixTimeSeconds(E) + TimeSpan.FromHours(10);
    private static readonly long AsOf = (Made - Armory.RenderDelay).ToUnixTimeSeconds();

    private static CutoutMeta Render() => new() { W = 4, H = 8, TexW = 4, TexH = 8, Guid = A, LastModified = Made.ToString("R") };

    private static RosterCharacter Roster(string? gear = "1:100;3:0;5:300;15:400;16:500", long gearAt = E - 100, long gearReadAt = 0,
        long updated = E + 50, long lookAt = 0) =>
        new(A, "Karuzo Macphisto", "WARLOCK", "Troll", "Female", 25, updated, Gear: gear, GearAt: gearAt, GearReadAt: gearReadAt, LookAt: lookAt);

    private static PortraitJudge Judge(RosterCharacter? character, string? look = Look, IReadOnlyList<string>? skipped = null,
        params RosterCharacter[] more)
    {
        var record = new RenderRecord(A, "Karuzo Macphisto", 1, DateTime.Now, E, null, null, null, null, "a.lua", look);
        var rosters = new List<RosterStore>();
        if (character is not null) rosters.Add(new RosterStore("a.lua", [character], new HashSet<string>()));
        if (more.Length > 0) rosters.Add(new RosterStore("b.lua", more, new HashSet<string>()));
        return PortraitJudge.From(new SavedVariablesSnapshot([new PortraitStore("a.lua", 1, [record])], rosters, skipped ?? []));
    }

    private static bool RenderWins(PortraitJudge judge) => judge.RenderWins(A, E, Render());

    [Fact]
    public void The_same_look_keeps_the_capture_however_new_the_render()
    {
        Assert.True(Armory.RenderWins(E, Render()));                          // the old rule took the render
        Assert.False(RenderWins(Judge(Roster())));
        // A slot one side lacks is not a change: the shirt and tabard, which the roster never has, and a slot the roster lacks.
        Assert.False(RenderWins(Judge(Roster(gear: "1:100;5:300"))));
    }

    [Theory]
    [InlineData(-1, true)]     // changed before the time the render shows: it shows the change
    [InlineData(+1, false)]    // changed after: the render does not show it yet, the capture stays
    public void Other_gear_takes_the_render_once_it_shows_the_change(int side, bool render)
    {
        var gearAt = AsOf + side * 60;
        Assert.Equal(render, RenderWins(Judge(Roster(gear: "1:100;3:0;5:301;15:400;16:500", gearAt: gearAt, updated: gearAt))));
        // Taken off: an empty slot is a change too, and so is a hidden helm taken off.
        Assert.Equal(render, RenderWins(Judge(Roster(gear: "1:100;3:0;5:0;15:400;16:500", gearAt: gearAt, updated: gearAt))));
        // h on one side, 0 on the other is an item taken off, not one shown or hidden: gearAt, not the later read.
        Assert.Equal(render, RenderWins(Judge(Roster(gear: "1:0;3:0;5:300;15:400;16:500", gearAt: gearAt, gearReadAt: AsOf + 3600, updated: gearAt),
            look: "1:h;3:0;5:300")));
    }

    [Theory]
    [InlineData(-1, true)]
    [InlineData(+1, false)]
    public void Only_a_helm_shown_or_hidden_dates_from_the_last_gear_read_not_from_gear_at(int side, bool render)
    {
        // gearAt is old (the items did not change); the read of the session the helm was hidden in decides.
        var read = AsOf + side * 60;
        Assert.Equal(render, RenderWins(Judge(Roster(gear: "1:h;3:0;5:300;15:400;16:500", gearAt: E - 100, gearReadAt: read, updated: read + 600))));
        // No gearReadAt: lastUpdate, which may wait a session too many but is never early.
        Assert.Equal(render, RenderWins(Judge(Roster(gear: "1:100;3:0;5:300;15:h;16:500", gearAt: E - 100, updated: read), look: "1:100;15:400")));
        Assert.Equal(render, RenderWins(Judge(Roster(gear: "1:100;3:0;5:300;15:400;16:500", gearAt: E - 100, updated: read), look: "1:h;15:400")));
    }

    [Theory]
    [InlineData(-1, true)]
    [InlineData(+1, false)]
    public void An_appearance_from_the_barber_after_the_capture_is_a_change_dated_by_look_at(int side, bool render)
    {
        var lookAt = AsOf + side * 60;
        Assert.Equal(render, RenderWins(Judge(Roster(lookAt: lookAt))));
        // Before the capture: the capture shows it.
        Assert.False(RenderWins(Judge(Roster(lookAt: E - 60))));
    }

    [Fact]
    public void A_roster_older_than_the_capture_is_no_change_and_a_render_from_before_the_capture_never_wins()
    {
        // Codex on #68: a capture at E showing the helm hidden, the roster's last read two hours
        // before it showing the helm, and a render as of an hour before the capture.
        var before = new CutoutMeta
        {
            W = 4, H = 8, TexW = 4, TexH = 8, Guid = A,
            LastModified = (DateTimeOffset.FromUnixTimeSeconds(E - 3600) + Armory.RenderDelay).ToString("R"),
        };
        var stale = Judge(Roster(gear: "1:100;3:0;5:300;15:400;16:500", gearAt: E - 9000, gearReadAt: E - 7200, updated: E - 7200), look: "1:h;5:300");
        Assert.False(stale.RenderWins(A, E, before));
        // An item the roster saw before the capture, likewise, and with a newer render too.
        var item = Judge(Roster(gear: "1:100;3:0;5:301;15:400;16:500", gearAt: E - 7200, updated: E - 7200));
        Assert.False(item.RenderWins(A, E, before));
        Assert.False(RenderWins(item));
        // Seen in the capture's own second: at, not after, so not a change since it.
        Assert.False(RenderWins(Judge(Roster(gear: "1:100;3:0;5:301;15:400;16:500", gearAt: E, updated: E))));
        // A change after it still counts, beside a stale one.
        Assert.True(RenderWins(Judge(Roster(gear: "1:101;3:0;5:301;15:400;16:500", gearAt: AsOf - 60, gearReadAt: E - 7200, updated: AsOf - 60),
            look: "1:100;5:300")));
    }

    [Fact]
    public void Both_changed_waits_for_the_later()
    {
        Assert.False(RenderWins(Judge(Roster(gear: "1:101;3:0;5:300;15:400;16:500", gearAt: AsOf - 60, lookAt: AsOf + 60))));
        Assert.False(RenderWins(Judge(Roster(gear: "1:101;3:0;5:300;15:400;16:500", gearAt: AsOf + 60, lookAt: AsOf - 60))));
        Assert.True(RenderWins(Judge(Roster(gear: "1:101;3:0;5:300;15:400;16:500", gearAt: AsOf - 60, lookAt: AsOf - 30))));
        // Two slots, two times: the head's item (gearAt, before) and the cloak hidden (the read, after). The later.
        Assert.False(RenderWins(Judge(Roster(gear: "1:101;3:0;5:300;15:h;16:500", gearAt: AsOf - 60, gearReadAt: AsOf + 60, updated: AsOf + 600))));
    }

    [Fact]
    public void The_gear_is_the_most_recently_updated_record_s()
    {
        var older = Roster(gear: "1:101;3:0;5:300;15:400;16:500", gearAt: AsOf - 60, updated: E + 10);
        Assert.False(RenderWins(Judge(older, more: Roster(updated: E + 50))));
        Assert.True(RenderWins(Judge(Roster(), more: older with { Updated = E + 90 })));
    }

    [Fact]
    public void What_cannot_be_told_is_the_old_estimate()
    {
        var newer = Armory.RenderWins(E, Render());
        Assert.True(newer);
        Assert.Equal(newer, RenderWins(Judge(Roster(), look: null)));                  // a record from before look
        Assert.Equal(newer, RenderWins(Judge(Roster(gear: null))));                    // no gear in the roster
        Assert.Equal(newer, RenderWins(Judge(null)));                                  // not in the roster
        Assert.Equal(newer, RenderWins(Judge(Roster(), skipped: ["b.lua"])));          // an account file not read
        Assert.Equal(newer, RenderWins(Judge(Roster(gear: "1:101;3:0;5:300;15:400;16:500", gearAt: 0, updated: 0)))); // changed, no time
        Assert.Equal(newer, RenderWins(PortraitJudge.Unknown));
        Assert.Equal(newer, Judge(Roster()).RenderWins(A, E + 1, Render()));          // another capture than the record's
        // Never captured: the render. A render with no date: the capture.
        Assert.True(Judge(Roster()).RenderWins(A, null, Render()));
        Assert.False(Judge(Roster(gear: "1:101", gearAt: 1)).RenderWins(A, E, Render() with { LastModified = null }));
    }

    [Fact]
    public void The_roster_s_gear_and_the_capture_s_look_are_read_from_the_file()
    {
        var text = "AltStableDB = {\n[\"" + A + "\"] = {\n[\"name\"] = \"Karuzo Macphisto\",\n[\"level\"] = 25,\n[\"lastUpdate\"] = 1794400050,\n"
            + "[\"gearid_head\"] = 100,\n[\"gearid_neck\"] = 55,\n[\"gearid_shoulder\"] = 0,\n[\"gearid_back\"] = 400,\n[\"gearid_mainhand\"] = 500,\n"
            + "[\"hidehelm\"] = 1,\n[\"hidecloak\"] = 0,\n[\"gearAt\"] = 1794399900,\n[\"gearReadAt\"] = 1794400040,\n[\"lookAt\"] = 1794390000,\n},\n}\n";
        var c = Assert.Single(SavedVariablesReader.ParseRoster(text, "a.lua").Characters);
        // The neck is not shown; the hidden helm is h; an empty slot 0.
        Assert.Equal("1:h;3:0;15:400;16:500", c.Gear);
        Assert.Equal((1794399900L, 1794400040L, 1794390000L), (c.GearAt, c.GearReadAt, c.LookAt));
        // The hidden cloak too, and a record with no gear at all.
        Assert.Equal("15:h", PortraitJudge.GearOf(k => k switch { "gearid_back" => 400, "hidecloak" => 1, _ => null }));
        Assert.Null(PortraitJudge.GearOf(_ => null));
        // No helm, hidden or not: empty.
        Assert.Equal("1:0", PortraitJudge.GearOf(k => k switch { "gearid_head" => 0, "hidehelm" => 1, _ => null }));

        var store = SavedVariablesReader.Parse(TestData.SavedVariables([
            TestData.Record("Karuzo Macphisto", A, 1, new DateTime(2026, 11, 11, 4, 0, 0)).Replace("[\"shot\"]", "[\"look\"] = \"" + Look + "\",\n[\"shot\"]"),
        ]), "a.lua");
        Assert.Equal(Look, Assert.Single(store!.Renders).Look);
    }
}

/// <summary>The rule through a pass: the manifest, its enhanced picture, and the Help page's warnings.</summary>
public class PortraitChoiceTests
{
    private const string A = "Player-6796-000A598A";
    private static readonly DateTime At = new(2026, 11, 11, 4, 0, 0);
    private static readonly long E = TestData.Epoch(At);

    // The account file: the capture's two records with their look, and the roster's record.
    private static string Store(string gear, long gearAt) =>
        "AltStableDB = {\n[\"" + A + "\"] = {\n[\"name\"] = \"Karuzo Macphisto\",\n[\"level\"] = 25,\n[\"lastUpdate\"] = " + gearAt + ",\n"
        + gear + "[\"gearAt\"] = " + gearAt + ",\n},\n}\nAltStableConfig = {\n[\"hiddenCharacters\"] = {\n},\n}\n"
        + "AltStablePortraits = {\n[\"version\"] = 1,\n[\"renders\"] = {\n"
        + TestData.Record("Karuzo Macphisto", A, 1, At).Replace("[\"shot\"]", "[\"look\"] = \"1:100;5:300\",\n[\"shot\"]") + ",\n"
        + TestData.Record("Karuzo Macphisto", A, 2, At.AddSeconds(1)).Replace("[\"shot\"]", "[\"look\"] = \"1:100;5:300\",\n[\"shot\"]") + ",\n},\n}\n";

    [Fact]
    public void A_render_of_the_same_look_leaves_the_capture_and_its_enhanced_picture_and_a_change_brings_the_render_without_a_warning()
    {
        using var t = new TempInstall();
        Directory.CreateDirectory(Path.Combine(t.Install.FlavorDir, "WTF"));
        File.WriteAllText(Path.Combine(t.Install.FlavorDir, "WTF", "Config.wtf"), "SET portal \"US\"\n");
        var folder = new CutoutFolder(t.Install.CutoutAddonDir);
        folder.WriteCutout("karuzo", TestData.Solid(4, 8, 1, 2, 3), new CutoutMeta { W = 4, H = 8, TexW = 4, TexH = 8, Guid = A, Epoch = E });
        var picture = TestData.Solid(4, 8, 200, 0, 0);
        var ms = new MemoryStream();
        TgaCodec.Write(ms, picture);
        folder.WriteEnhanced("karuzo", picture, new CutoutMeta
        {
            W = 4, H = 8, TexW = 4, TexH = 8, Guid = A,
            Enhancement = new EnhancementMeta
            {
                SourceHash = EnhancementSignature.HashOf(Path.Combine(folder.CutoutsDir, "karuzo.tga")),
                OutputHash = EnhancementSignature.HashOf(ms.ToArray()),
            },
        });
        // A render of later play, the same gear.
        var made = DateTimeOffset.FromUnixTimeSeconds(E) + TimeSpan.FromHours(10);
        folder.WriteArmory("karuzo", ArmoryFolderTests.Tga(4, 8, 9), new CutoutMeta
        {
            W = 4, H = 8, TexW = 4, TexH = 8, Guid = A, Url = Armory.Url(A, false), LastModified = made.ToString("R"),
        });
        const string same = "[\"gearid_head\"] = 100,\n[\"gearid_chest\"] = 300,\n";
        t.WriteStore("1#1", Store(same, E + 60));

        var report = new ConvertPass(t.Install, new ConvertOptions(Armory: true)).Run();
        var lua = File.ReadAllText(folder.ManifestPath);
        Assert.Contains(@"Cutouts\karuzo.tga]]", lua);
        Assert.Contains(@"Cutouts\Enhanced\karuzo.tga]]", lua);
        Assert.DoesNotContain(@"Cutouts\Armory\karuzo.tga]]", lua);
        Assert.Contains("armory = true", lua);                        // the armory still keeps the character
        Assert.DoesNotContain(report.Warnings, w => w.Contains("not drawn", StringComparison.Ordinal));

        // A new chest, before the time the render shows: the render, plain, while its own enhanced picture waits.
        const string other = "[\"gearid_head\"] = 100,\n[\"gearid_chest\"] = 301,\n";
        t.WriteStore("1#1", Store(other, E + 120));
        report = new ConvertPass(t.Install, new ConvertOptions(Armory: true)).Run();
        lua = File.ReadAllText(folder.ManifestPath);
        Assert.Contains(@"Cutouts\Armory\karuzo.tga]]", lua);
        Assert.DoesNotContain(@"Cutouts\Enhanced\karuzo.tga]]", lua);
        Assert.DoesNotContain(report.Warnings, w => w.Contains("not drawn", StringComparison.Ordinal));
        // The warning is still there for a capture whose enhanced picture is not its own.
        Assert.NotNull(folder.Attached("karuzo", Path.Combine(folder.CutoutsDir, "karuzo.tga"), A));
        var said = new List<string>();
        Assert.Null(folder.Attached("karuzo", Path.Combine(folder.ArmoryDir, "karuzo.tga"), A, said.Add));
        Assert.Contains(said, w => w.Contains("is not the portrait it was made from", StringComparison.Ordinal));
    }

    [Fact]
    public void A_capture_s_picture_that_a_render_s_took_the_file_of_is_rebuilt_from_its_original_with_no_codex_run()
    {
        // The owner's Kalizo, Karuzo and Morphisto after 1.2.1: 1.2.0 made the render's picture
        // over the capture's; the capture is the portrait again, its picture "made", drawn plain.
        using var t = new TempInstall();
        var at = DateTime.Now.AddDays(-2);
        var epoch = TestData.Epoch(at);
        string Record(int shot, DateTime when) =>
            TestData.Record("Karuzo Macphisto", A, shot, when, epoch).Replace("[\"shot\"]", "[\"look\"] = \"1:100;5:300\",\n[\"shot\"]");
        t.WriteStore("1#1", "AltStableDB = {\n[\"" + A + "\"] = {\n[\"name\"] = \"Karuzo Macphisto\",\n[\"class\"] = \"WARLOCK\",\n[\"race\"] = \"Troll\",\n"
            + "[\"gender\"] = \"Female\",\n[\"level\"] = 25,\n[\"lastUpdate\"] = " + (epoch + 60) + ",\n[\"gearAt\"] = " + (epoch - 600) + ",\n"
            + "[\"gearid_head\"] = 100,\n[\"gearid_chest\"] = 300,\n},\n}\nAltStableConfig = {\n[\"hiddenCharacters\"] = {\n},\n}\n"
            + "AltStablePortraits = {\n[\"version\"] = 1,\n[\"renders\"] = {\n" + Record(1, at) + ",\n" + Record(2, at.AddSeconds(1)) + ",\n},\n}\n");
        Directory.CreateDirectory(Path.Combine(t.Install.FlavorDir, "WTF"));
        File.WriteAllText(Path.Combine(t.Install.FlavorDir, "WTF", "Config.wtf"), "SET portal \"US\"\n");
        var toc = Path.Combine(t.Install.AddOnsDir, "AltStableRoster", "AltStableRoster.toc");
        Directory.CreateDirectory(Path.GetDirectoryName(toc)!);
        File.WriteAllText(toc, "## X-AltStable-Enhanced: 1\n");
        var folder = new CutoutFolder(t.Install.CutoutAddonDir);
        folder.EnsureToc();
        var capture = Path.Combine(folder.CutoutsDir, "karuzo-macphisto.tga");
        folder.WriteCutout("karuzo-macphisto", TestData.Solid(4, 8, 77, 30, 30),
            new CutoutMeta { W = 4, H = 8, TexW = 4, TexH = 8, Guid = A, Epoch = epoch });
        folder.WriteArmory("karuzo-macphisto", ArmoryFolderTests.Tga(4, 8, 9), new CutoutMeta
        {
            W = 4, H = 8, TexW = 4, TexH = 8, Guid = A, Url = Armory.Url(A, false), ETag = "\"v1\"", LastModified = DateTimeOffset.UtcNow.ToString("R"),
        });
        var render = Path.Combine(folder.ArmoryDir, "karuzo-macphisto.tga");
        var cdn = new FakeCdn { Respond = _ => new HttpResponseMessage(System.Net.HttpStatusCode.NotModified) };
        PortraitJudge Judge() => PortraitJudge.From(SavedVariablesReader.Snapshot(t.Install.AccountsDir));
        EnhancedTexture? Drawn() => folder.Inventory(Judge()).Single().Enhanced;

        // The capture's picture, made by the app: its record and its original are real.
        var first = new ArmoryEnhanceTests.FakeCodex();
        using (var c = ArmoryWorkerTests.Started(t, cdn, enhance: true, hooks: first.Hooks))
        {
            ArmoryWorkerTests.Until(() => Drawn() is not null && first.Calls == 1, "the capture's picture");
            // Kept just after the picture is written.
            ArmoryWorkerTests.Until(() => AttemptHistory.Load(folder.EnhancedDir, A).LastWritten is { } w
                && File.Exists(Path.Combine(ArmoryWorkerTests.Data(t), "originals", w.Signature + ".png")), "its original kept");
        }
        var made = AttemptHistory.Load(folder.EnhancedDir, A).LastWritten!;

        // 1.2.0: the render's picture written over it, and recorded.
        var picture = TestData.Solid(4, 8, 200, 0, 0);
        var ms = new MemoryStream();
        TgaCodec.Write(ms, picture);
        folder.WriteEnhanced("karuzo-macphisto", picture, new CutoutMeta
        {
            W = 4, H = 8, TexW = 4, TexH = 8, Guid = A,
            Enhancement = new EnhancementMeta { SourceHash = EnhancementSignature.HashOf(render), OutputHash = EnhancementSignature.HashOf(ms.ToArray()), Style = "wow-like" },
        });
        var history = AttemptHistory.Load(folder.EnhancedDir, A);
        history.Begin(new Attempt { Signature = "render-sig", Base = "karuzo-macphisto", Started = DateTime.Now, Style = "wow-like",
            SourceHash = EnhancementSignature.HashOf(render), Source = Attempt.FromArmory });
        history.End("render-sig", Attempt.Written, DateTime.Now, EnhancementSignature.HashOf(ms.ToArray()));
        Assert.Null(Drawn());

        // The next start, enhancing off - a rebuild spends nothing: rebuilt from the original, drawn with the capture.
        var second = new ArmoryEnhanceTests.FakeCodex();
        using (var c = ArmoryWorkerTests.Started(t, cdn, enhance: false, hooks: second.Hooks))
        {
            ArmoryWorkerTests.Until(() => Drawn() is not null, "the capture's picture, rebuilt");
            Assert.Equal(EnhancementSignature.HashOf(capture), folder.ReadEnhancedMeta("karuzo-macphisto")!.Enhancement!.SourceHash);
            Assert.Equal(made.Signature, folder.ReadEnhancedMeta("karuzo-macphisto")!.Enhancement!.Signature);
            ArmoryWorkerTests.Until(() => File.ReadAllText(folder.ManifestPath).Contains(@"Cutouts\Enhanced\karuzo-macphisto.tga]]"), "the manifest");
            Thread.Sleep(2500);
            // Once: drawn now, so not rebuilt at every look.
            Assert.Single(File.ReadAllLines(c.LogPath), l => l.Contains("rebuilt from its original (no Codex run)", StringComparison.Ordinal));
            // Another style: the picture made from this portrait stays, offered, as a style change is.
            c.SetEnhance(true, 10, EnhanceStyles.Realistic);
            ArmoryWorkerTests.Until(() => c.Current.Shell.EnhanceHeld is not null, "the new style, offered");
            Thread.Sleep(1500);
            Assert.Equal(0, second.Calls);
        }

        // No original kept (an older app, a cleaned folder): nothing to rebuild, and nothing said at every look.
        File.Delete(Path.Combine(ArmoryWorkerTests.Data(t), "originals", made.Signature + ".png"));
        folder.WriteEnhanced("karuzo-macphisto", picture, new CutoutMeta
        {
            W = 4, H = 8, TexW = 4, TexH = 8, Guid = A,
            Enhancement = new EnhancementMeta { SourceHash = EnhancementSignature.HashOf(render), OutputHash = EnhancementSignature.HashOf(ms.ToArray()), Style = "wow-like" },
        });
        using (var c = ArmoryWorkerTests.Started(t, cdn, enhance: false, hooks: second.Hooks))
        {
            ArmoryWorkerTests.Until(() => c.Current.Shell.Report is not null, "the first pass");
            Thread.Sleep(2500);
            Assert.Null(Drawn());
            Assert.DoesNotContain("could not be rebuilt", File.ReadAllText(c.LogPath));
        }
    }

    [Fact]
    public void The_armory_worker_fetches_a_render_of_the_same_look_and_the_capture_stays_the_portrait_the_enhancer_enhances()
    {
        // The owner's 11 November run: a render of later play, nothing changed. 1.2.0 swapped
        // the capture for it, and enhanced the render.
        using var t = new TempInstall();
        var at = DateTime.Now.AddDays(-2);
        var epoch = TestData.Epoch(at);
        string Record(int shot, DateTime when) =>
            TestData.Record("Karuzo Macphisto", A, shot, when, epoch).Replace("[\"shot\"]", "[\"look\"] = \"1:100;5:300\",\n[\"shot\"]");
        t.WriteStore("1#1", "AltStableDB = {\n[\"" + A + "\"] = {\n[\"name\"] = \"Karuzo Macphisto\",\n[\"class\"] = \"WARLOCK\",\n[\"race\"] = \"Troll\",\n"
            + "[\"gender\"] = \"Female\",\n[\"level\"] = 25,\n[\"lastUpdate\"] = " + (epoch + 60) + ",\n[\"gearAt\"] = " + (epoch - 600) + ",\n"
            + "[\"gearid_head\"] = 100,\n[\"gearid_chest\"] = 300,\n},\n}\nAltStableConfig = {\n[\"hiddenCharacters\"] = {\n},\n}\n"
            + "AltStablePortraits = {\n[\"version\"] = 1,\n[\"renders\"] = {\n" + Record(1, at) + ",\n" + Record(2, at.AddSeconds(1)) + ",\n},\n}\n");
        Directory.CreateDirectory(Path.Combine(t.Install.FlavorDir, "WTF"));
        File.WriteAllText(Path.Combine(t.Install.FlavorDir, "WTF", "Config.wtf"), "SET portal \"US\"\n");
        var toc = Path.Combine(t.Install.AddOnsDir, "AltStableRoster", "AltStableRoster.toc");
        Directory.CreateDirectory(Path.GetDirectoryName(toc)!);
        File.WriteAllText(toc, "## X-AltStable-Enhanced: 1\n");
        var folder = new CutoutFolder(t.Install.CutoutAddonDir);
        folder.EnsureToc();
        var capture = Path.Combine(folder.CutoutsDir, "karuzo-macphisto.tga");
        folder.WriteCutout("karuzo-macphisto", TestData.Solid(4, 8, 77, 30, 30),
            new CutoutMeta { W = 4, H = 8, TexW = 4, TexH = 8, Guid = A, Epoch = epoch });
        // The render is there before the enhancer first looks: the plan and the launch both choose.
        folder.WriteArmory("karuzo-macphisto", ArmoryFolderTests.Tga(4, 8, 9), new CutoutMeta
        {
            W = 4, H = 8, TexW = 4, TexH = 8, Guid = A, Url = Armory.Url(A, false), ETag = "\"v1\"", LastModified = DateTimeOffset.UtcNow.ToString("R"),
        });

        var cdn = new FakeCdn { Respond = _ => new HttpResponseMessage(System.Net.HttpStatusCode.NotModified) };
        var codex = new ArmoryEnhanceTests.FakeCodex();
        using var c = ArmoryWorkerTests.Started(t, cdn, enhance: true, hooks: codex.Hooks);
        ArmoryWorkerTests.Until(() => cdn.Count == 1, "the render asked about");
        ArmoryWorkerTests.Until(() => folder.ReadEnhancedMeta("karuzo-macphisto")?.Enhancement is not null && codex.Calls == 1, "one enhanced picture");
        Thread.Sleep(1500);
        // Made from the capture, not the render; and the manifest names the capture, with it.
        Assert.Equal(EnhancementSignature.HashOf(capture), folder.ReadEnhancedMeta("karuzo-macphisto")!.Enhancement!.SourceHash);
        Assert.Equal(1, codex.Calls);
        var lua = File.ReadAllText(folder.ManifestPath);
        Assert.Contains(@"Cutouts\karuzo-macphisto.tga]]", lua);
        Assert.Contains(@"Cutouts\Enhanced\karuzo-macphisto.tga]]", lua);
        Assert.DoesNotContain(@"Cutouts\Armory\karuzo-macphisto.tga]]", lua);

        // "Check now" while the capture stays: nothing to enhance. The yes was for that render:
        // a later one that shows a change waits, offered (not on their own), as before.
        c.SetArmoryEnhance(auto: false, limit: true, max: 5);
        c.CheckArmoryNow(A);
        ArmoryWorkerTests.Until(() => cdn.Count == 2, "the armory asked again");
        Thread.Sleep(1500);
        Assert.Equal(1, codex.Calls);
        var changedAt = DateTimeOffset.UtcNow.AddHours(-8).ToUnixTimeSeconds();
        t.WriteStore("1#1", "AltStableDB = {\n[\"" + A + "\"] = {\n[\"name\"] = \"Karuzo Macphisto\",\n[\"class\"] = \"WARLOCK\",\n[\"race\"] = \"Troll\",\n"
            + "[\"gender\"] = \"Female\",\n[\"level\"] = 25,\n[\"lastUpdate\"] = " + changedAt + ",\n[\"gearAt\"] = " + changedAt + ",\n"
            + "[\"gearid_head\"] = 100,\n[\"gearid_chest\"] = 301,\n},\n}\nAltStableConfig = {\n[\"hiddenCharacters\"] = {\n},\n}\n"
            + "AltStablePortraits = {\n[\"version\"] = 1,\n[\"renders\"] = {\n" + Record(1, at) + ",\n" + Record(2, at.AddSeconds(1)) + ",\n},\n}\n");
        folder.WriteArmory("karuzo-macphisto", ArmoryFolderTests.Tga(4, 8, 50), new CutoutMeta
        {
            W = 4, H = 8, TexW = 4, TexH = 8, Guid = A, Url = Armory.Url(A, false), ETag = "\"v2\"", LastModified = DateTimeOffset.UtcNow.ToString("R"),
        });
        c.ConvertNow();
        ArmoryWorkerTests.Until(() => File.ReadAllText(folder.ManifestPath).Contains(@"Cutouts\Armory\karuzo-macphisto.tga]]"), "the render, now the portrait");
        Thread.Sleep(2500);
        Assert.Equal(1, codex.Calls);
    }
}
