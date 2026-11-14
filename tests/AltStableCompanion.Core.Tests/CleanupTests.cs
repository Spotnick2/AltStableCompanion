using AltStableCompanion.Core;
using Xunit;

namespace AltStableCompanion.Core.Tests;

/// <summary>Characters AltStable no longer has (#77): found, offered, and their files cleaned up on a yes.</summary>
public class CleanupTests
{
    private const string A = "Player-6796-000A598A";   // in AltStable
    private const string G = "Player-6796-000060C5";   // the beta Karuzo: gone

    private static readonly HashSet<string> Roster = new(StringComparer.Ordinal) { A };

    // The manifest keys an entry by its GUID.
    private static ManifestEntry Entry(string fileBase, string guid) => new(guid, guid, fileBase + ".tga", 4, 8, 4, 8, null, null);

    private static CharacterStatus Status(string guid, string name, CharacterState state) =>
        new(guid, name, new DateTime(2026, 10, 21, 20, 58, 9), state, null);

    [Fact]
    public void A_character_no_roster_has_is_gone_while_it_has_a_portrait_and_not_listed_once_it_has_none()
    {
        var entries = new[] { Entry("karuzo-macphisto-0a598a", A), Entry("karuzo-macphisto", G) };
        var captures = new[] { Status(A, "Karuzo Macphisto", CharacterState.Portrait), Status(G, "Karuzo Macphisto", CharacterState.Portrait) };
        var rows = Collection.Build(entries, captures, new Dictionary<string, DateTime>(), roster: Roster);
        Assert.False(rows.Single(r => r.Guid == A).Gone);
        var gone = rows.Single(r => r.Guid == G);
        Assert.True(gone.Gone);
        Assert.Equal(PassText.GoneRow, PassText.RowSummary(gone, DateTime.Today));
        Assert.Equal("1", PassText.Count(rows));                                  // the count is AltStable's characters
        Assert.Equal("1 character AltStable no longer has", PassText.GoneLine(rows));
        Assert.Equal(["Karuzo Macphisto · Player-6796-000060C5 · captured 2026-10-21"], PassText.CleanUpLines(rows));

        // Its files cleaned up, its records still in AltStable.lua: not listed, and not a problem.
        var after = Collection.Build([entries[0]], [captures[0], Status(G, "Karuzo Macphisto", CharacterState.Missing)],
            new Dictionary<string, DateTime>(), roster: Roster);
        Assert.DoesNotContain(after, r => r.Guid == G);
        Assert.Null(PassText.Attention(after));
        Assert.Null(PassText.GoneLine(after));

        // A roster that could not be read says nothing of who is gone.
        var unknown = Collection.Build(entries, captures, new Dictionary<string, DateTime>(), roster: null);
        Assert.DoesNotContain(unknown, r => r.Gone);
        Assert.Equal("2", PassText.Count(unknown));
        // A gone row is never "needs attention": the clean-up is its action.
        Assert.False((gone with { Outcome = CaptureOutcome.Failed }).NeedsAttention);
    }

    [Fact]
    public void The_roster_is_every_account_s_characters_or_nothing_when_a_file_could_not_be_read()
    {
        RosterStore Store(string path, string guid, string? problem = null) =>
            new(path, [new RosterCharacter(guid, "X", null, null, null, 1, 0)], new HashSet<string> { guid }, problem);
        var both = Leftovers.RosterGuids(new SavedVariablesSnapshot([], [Store("a", A), Store("b", G)], []));
        Assert.Equal([G, A], both!.Order(StringComparer.Ordinal));               // hidden ones are still AltStable's
        Assert.Null(Leftovers.RosterGuids(new SavedVariablesSnapshot([], [Store("a", A)], ["b.lua"])));
        Assert.Null(Leftovers.RosterGuids(new SavedVariablesSnapshot([], [Store("a", A, "stopped mid-way")], [])));
    }

    // A gone character's files and another's, side by side.
    private static (CutoutFolder Folder, string Originals, string[] Gone, string[] Kept) Files(TempInstall t)
    {
        var folder = new CutoutFolder(t.Install.CutoutAddonDir);
        var originals = Path.Combine(t.Root, "data", "originals");
        Directory.CreateDirectory(originals);
        var shots = new[] { "WoWScrnShot_102126_205809.tga", "WoWScrnShot_102126_205810.tga" };
        foreach (var s in shots) File.WriteAllText(Path.Combine(t.Install.Screenshots, s), "shot");
        File.WriteAllText(Path.Combine(t.Install.Screenshots, "WoWScrnShot_mine.tga"), "the player's own");
        folder.WriteCutout("karuzo-macphisto", TestData.Solid(4, 8, 1, 2, 3),
            new CutoutMeta { W = 4, H = 8, TexW = 4, TexH = 8, Guid = G, Epoch = 1792630689, Shots = [.. shots] });
        folder.WriteEnhanced("karuzo-macphisto", TestData.Solid(4, 8, 9, 9, 9), new CutoutMeta { W = 4, H = 8, TexW = 4, TexH = 8, Guid = G });
        folder.WriteArmory("karuzo-macphisto", ArmoryFolderTests.Tga(4, 8, 7), new CutoutMeta { W = 4, H = 8, TexW = 4, TexH = 8, Guid = G });
        folder.WriteCutout("karuzo-macphisto-0a598a", TestData.Solid(4, 8, 5, 5, 5),
            new CutoutMeta { W = 4, H = 8, TexW = 4, TexH = 8, Guid = A, Epoch = 1794400000, Shots = ["WoWScrnShot_mine.tga"] });
        folder.WriteEnhanced("karuzo-macphisto-0a598a", TestData.Solid(4, 8, 6, 6, 6), new CutoutMeta { W = 4, H = 8, TexW = 4, TexH = 8, Guid = A });
        // Histories: G's own original, one both name (never deleted), and A's.
        void History(string guid, params string[] signatures)
        {
            var h = AttemptHistory.Load(folder.EnhancedDir, guid);
            foreach (var s in signatures)
            {
                h.Begin(new Attempt { Signature = s, Base = "x", Started = DateTime.Now });
                h.End(s, Attempt.Written, DateTime.Now);
                File.WriteAllText(Path.Combine(originals, s + ".png"), "png");
            }
        }
        History(G, "gsig", "shared");
        History(A, "asig", "shared");
        string C(string name) => Path.Combine(folder.CutoutsDir, name);
        string[] gone =
        [
            C("karuzo-macphisto.tga"), C("karuzo-macphisto.json"),
            Path.Combine(t.Install.Screenshots, shots[0]), Path.Combine(t.Install.Screenshots, shots[1]),
            Path.Combine(folder.ArmoryDir, "karuzo-macphisto.tga"), Path.Combine(folder.ArmoryDir, "karuzo-macphisto.json"),
            Path.Combine(folder.EnhancedDir, "karuzo-macphisto.tga"), Path.Combine(folder.EnhancedDir, "karuzo-macphisto.json"),
            AttemptHistory.PathFor(folder.EnhancedDir, G), Path.Combine(originals, "gsig.png"),
        ];
        string[] kept =
        [
            C("karuzo-macphisto-0a598a.tga"), C("karuzo-macphisto-0a598a.json"),
            Path.Combine(folder.EnhancedDir, "karuzo-macphisto-0a598a.tga"), Path.Combine(folder.EnhancedDir, "karuzo-macphisto-0a598a.json"),
            Path.Combine(t.Install.Screenshots, "WoWScrnShot_mine.tga"), AttemptHistory.PathFor(folder.EnhancedDir, A),
            Path.Combine(originals, "asig.png"), Path.Combine(originals, "shared.png"),
        ];
        return (folder, originals, gone, kept);
    }

    [Fact]
    public void A_character_s_files_are_its_cutouts_their_screenshots_its_pictures_its_history_and_its_own_originals()
    {
        using var t = new TempInstall();
        var (folder, originals, gone, kept) = Files(t);
        var files = Leftovers.FilesOf(G, folder, t.Install.Screenshots, originals);
        Assert.Equal(gone.Order(StringComparer.OrdinalIgnoreCase), files.Order(StringComparer.OrdinalIgnoreCase));
        Assert.Empty(files.Intersect(kept, StringComparer.OrdinalIgnoreCase));
        // Another history that cannot be read may name its originals: they stay.
        File.WriteAllText(AttemptHistory.PathFor(folder.EnhancedDir, "Player-1-00000001"), "{ not json");
        Assert.DoesNotContain(Path.Combine(originals, "gsig.png"), Leftovers.FilesOf(G, folder, t.Install.Screenshots, originals));
        // Not a GUID: nothing.
        Assert.Empty(Leftovers.FilesOf(@"..\x", folder, t.Install.Screenshots, originals));
    }

    // The account file: A in the roster; the records of both A's and G's captures.
    private static string Store(bool gIsBack = false)
    {
        static string Db(string guid, string name) =>
            "[\"" + guid + "\"] = {\n[\"name\"] = \"" + name + "\",\n[\"level\"] = 25,\n[\"lastUpdate\"] = 1794400050,\n},\n";
        return "AltStableDB = {\n" + Db(A, "Karuzo Macphisto") + (gIsBack ? Db(G, "Karuzo Macphisto") : "") + "}\n"
            + "AltStableConfig = {\n[\"hiddenCharacters\"] = {\n},\n}\n"
            + "AltStablePortraits = {\n[\"version\"] = 1,\n[\"renders\"] = {\n"
            + TestData.Record("Karuzo Macphisto", G, 1, new DateTime(2026, 10, 21, 20, 58, 9), 1792630689) + ",\n"
            + TestData.Record("Karuzo Macphisto", G, 2, new DateTime(2026, 10, 21, 20, 58, 10), 1792630690) + ",\n"
            + TestData.Record("Karuzo Macphisto", A, 1, new DateTime(2026, 11, 10, 16, 7, 0), 1794400000) + ",\n"
            + TestData.Record("Karuzo Macphisto", A, 2, new DateTime(2026, 11, 10, 16, 7, 1), 1794400001) + ",\n"
            + "},\n}\n";
    }

    [Fact]
    public async Task The_clean_up_deletes_the_gone_character_s_files_on_a_yes_and_the_list_no_longer_has_it()
    {
        using var t = new TempInstall();
        var (folder, _, gone, kept) = Files(t);
        t.WriteStore("1#1", Store());
        using var c = ArmoryWorkerTests.Started(t, new FakeCdn(), armory: false);
        ArmoryWorkerTests.Until(() => c.Current.Shell.Report?.Portraits?.Any(r => r.Guid == G && r.Gone) == true, "the gone row");
        Assert.All(gone.Concat(kept), f => Assert.True(File.Exists(f), f));    // found, nothing deleted before the yes

        var said = await c.CleanUpCharactersAsync([G]);
        Assert.Equal("Deleted 10 files of 1 character.", said);
        Assert.All(gone, f => Assert.False(File.Exists(f), f));
        Assert.All(kept, f => Assert.True(File.Exists(f), f));
        Assert.DoesNotContain(c.Current.Shell.Report!.Portraits!, r => r.Guid == G);
        Assert.DoesNotContain("karuzo-macphisto.tga]]", File.ReadAllText(folder.ManifestPath));
        Assert.Contains("clean-up: deleted ", File.ReadAllText(c.LogPath));

        // The next pass: its records are still in AltStable.lua, and it does not come back.
        c.ConvertNow();
        Thread.Sleep(2000);
        Assert.DoesNotContain(c.Current.Shell.Report!.Portraits!, r => r.Guid == G);
        Assert.Null(PassText.Attention(c.Current.Shell.Report.Portraits!));
    }

    [Fact]
    public async Task A_character_back_in_the_roster_since_the_question_keeps_everything()
    {
        using var t = new TempInstall();
        var (_, _, gone, _) = Files(t);
        t.WriteStore("1#1", Store());
        using var c = ArmoryWorkerTests.Started(t, new FakeCdn(), armory: false);
        ArmoryWorkerTests.Until(() => c.Current.Shell.Report?.Portraits?.Any(r => r.Guid == G && r.Gone) == true, "the gone row");
        t.WriteStore("1#1", Store(gIsBack: true));
        Assert.Equal("Nothing deleted. 1 is in AltStable again, so it was kept.", await c.CleanUpCharactersAsync([G]));
        Assert.All(gone, f => Assert.True(File.Exists(f), f));
    }

    [Fact]
    public void Leaving_them_out_is_a_setting_that_deletes_nothing()
    {
        using var t = new TempInstall();
        t.WriteStore("1#1", Store());
        using var c = ArmoryWorkerTests.Started(t, new FakeCdn(), armory: false);
        Assert.False(c.Current.HideGone);
        c.SetHideGone(true);
        Assert.True(c.Current.HideGone);
        Assert.True(Settings.Load(ArmoryWorkerTests.Data(t)).HideGoneCharacters);
        Assert.NotEqual(new Settings(), new Settings { HideGoneCharacters = true });
    }

    [Fact]
    public void The_clean_up_says_what_it_did()
    {
        Assert.Equal("Deleted 1 file of 1 character.", PassText.CleanUpResult(1, 1, 0, []));
        Assert.Equal("Deleted 9 files of 2 characters. 1 could not be deleted (a.tga); the log says why.", PassText.CleanUpResult(2, 9, 0, ["a.tga"]));
        Assert.Equal("Nothing to clean up: their files were already gone.", PassText.CleanUpResult(0, 0, 0, []));
        Assert.Equal("Nothing deleted. 2 are in AltStable again, so they were kept.", PassText.CleanUpResult(0, 0, 2, []));
    }
}
