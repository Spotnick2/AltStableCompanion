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
        var groups = Leftovers.FilesOf(G, folder, t.Install.Screenshots, originals);
        var files = groups.SelectMany(g => g.All).ToList();
        Assert.Equal(gone.Order(StringComparer.OrdinalIgnoreCase), files.Order(StringComparer.OrdinalIgnoreCase));
        Assert.Empty(files.Intersect(kept, StringComparer.OrdinalIgnoreCase));
        // Each owner after what it names, the history after its originals; the portraits after
        // everything else; and the enhanced picture before the cutout whose base finds it.
        var capture = groups.Single(g => g.Owner == gone[1]);
        Assert.Equal([gone[0]], capture.Files);
        Assert.Equal([gone[9]], groups.Single(g => g.Owner == gone[8]).Files);
        Assert.Equal([gone[2], gone[3]], groups.Single(g => g.Owner is null).Files);
        Assert.Equal([gone[4]], groups.Single(g => g.Owner == gone[5]).Files);
        Assert.Equal([false, false, false, true, true], groups.Select(g => g.Primary));
        Assert.True(groups.ToList().FindIndex(g => g.Owner == gone[7]) < groups.ToList().IndexOf(capture));
        // Another history that cannot be read may name its originals: they stay.
        File.WriteAllText(AttemptHistory.PathFor(folder.EnhancedDir, "Player-1-00000001"), "{ not json");
        Assert.DoesNotContain(Path.Combine(originals, "gsig.png"), Leftovers.FilesOf(G, folder, t.Install.Screenshots, originals).SelectMany(g => g.All));
        // Not a GUID: nothing.
        Assert.Empty(Leftovers.FilesOf(@"..\x", folder, t.Install.Screenshots, originals));
    }

    // The account file: A in the roster; the records of both A's and G's captures.
    private static string Store(bool gIsBack = false, bool gRecords = true)
    {
        static string Db(string guid, string name) =>
            "[\"" + guid + "\"] = {\n[\"name\"] = \"" + name + "\",\n[\"level\"] = 25,\n[\"lastUpdate\"] = 1794400050,\n},\n";
        return "AltStableDB = {\n" + Db(A, "Karuzo Macphisto") + (gIsBack ? Db(G, "Karuzo Macphisto") : "") + "}\n"
            + "AltStableConfig = {\n[\"hiddenCharacters\"] = {\n},\n}\n"
            + "AltStablePortraits = {\n[\"version\"] = 1,\n[\"renders\"] = {\n"
            + (gRecords ? TestData.Record("Karuzo Macphisto", G, 1, new DateTime(2026, 10, 21, 20, 58, 9), 1792630689) + ",\n"
                + TestData.Record("Karuzo Macphisto", G, 2, new DateTime(2026, 10, 21, 20, 58, 10), 1792630690) + ",\n" : "")
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

        var said = await c.CleanUpCharactersAsync(t.Install.FlavorDir, [G]);
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
        Assert.Equal("Nothing deleted. 1 is in AltStable again, so it was kept.", await c.CleanUpCharactersAsync(t.Install.FlavorDir, [G]));
        Assert.All(gone, f => Assert.True(File.Exists(f), f));
    }

    [Fact]
    public void An_account_whose_file_is_missing_or_has_no_roster_table_makes_the_roster_unknown()
    {
        // Codex on #78: the game swaps AltStable.lua through a .bak; an account caught between
        // the two is missing from the snapshot, and its characters are not gone for that.
        using var t = new TempInstall();
        t.WriteStore("1#1", Store());
        Assert.NotNull(Leftovers.Roster(t.Install.AccountsDir, SavedVariablesReader.Snapshot(t.Install.AccountsDir)));
        var other = Path.Combine(t.Install.AccountsDir, "2#2", "SavedVariables");
        Directory.CreateDirectory(other);
        File.WriteAllText(Path.Combine(other, "OtherAddon.lua"), "x = 1\n");
        // An account that never ran AltStable is no account of its: nothing missing.
        Assert.NotNull(Leftovers.Roster(t.Install.AccountsDir, SavedVariablesReader.Snapshot(t.Install.AccountsDir)));
        // One of its modules' files, or its own .bak, with no AltStable.lua: unknown.
        File.WriteAllText(Path.Combine(other, "AltStableSync.lua"), "AltStableSyncDB = {}\n");
        Assert.Null(Leftovers.Roster(t.Install.AccountsDir, SavedVariablesReader.Snapshot(t.Install.AccountsDir)));
        File.Delete(Path.Combine(other, "AltStableSync.lua"));
        File.WriteAllText(Path.Combine(other, "AltStable.lua.bak"), "");
        Assert.Null(Leftovers.Roster(t.Install.AccountsDir, SavedVariablesReader.Snapshot(t.Install.AccountsDir)));
        // A file with no AltStableDB table: not an empty roster.
        File.WriteAllText(Path.Combine(other, "AltStable.lua"), "AltStableConfig = {\n}\n");
        Assert.False(SavedVariablesReader.Snapshot(t.Install.AccountsDir).Rosters.Single(r => r.Path.Contains("2#2")).HasRoster);
        Assert.Null(Leftovers.Roster(t.Install.AccountsDir, SavedVariablesReader.Snapshot(t.Install.AccountsDir)));
        File.WriteAllText(Path.Combine(other, "AltStable.lua"), "AltStableDB = {\n}\n");
        Assert.NotNull(Leftovers.Roster(t.Install.AccountsDir, SavedVariablesReader.Snapshot(t.Install.AccountsDir)));
    }

    [Fact]
    public async Task A_missing_account_file_marks_nobody_and_deletes_nothing()
    {
        using var t = new TempInstall();
        var (_, _, gone, _) = Files(t);
        t.WriteStore("1#1", Store());
        var other = Path.Combine(t.Install.AccountsDir, "2#2", "SavedVariables");
        Directory.CreateDirectory(other);
        File.WriteAllText(Path.Combine(other, "AltStable.lua.bak"), "");
        using var c = ArmoryWorkerTests.Started(t, new FakeCdn(), armory: false);
        ArmoryWorkerTests.Until(() => c.Current.Shell.Report?.Portraits?.Any(r => r.Guid == G) == true, "the first pass");
        Assert.DoesNotContain(c.Current.Shell.Report!.Portraits!, r => r.Gone);
        Assert.StartsWith("Not cleaned up: an account file could not be read", await c.CleanUpCharactersAsync(t.Install.FlavorDir, [G]));
        Assert.All(gone, f => Assert.True(File.Exists(f), f));
    }

    [Fact]
    public async Task A_question_about_another_game_folder_deletes_nothing()
    {
        using var t = new TempInstall();
        var (_, _, gone, _) = Files(t);
        t.WriteStore("1#1", Store());
        using var c = ArmoryWorkerTests.Started(t, new FakeCdn(), armory: false);
        ArmoryWorkerTests.Until(() => c.Current.Shell.Report?.Portraits?.Any(r => r.Guid == G && r.Gone) == true, "the gone row");
        Assert.Equal("Not cleaned up: the game folder changed since the question.",
            await c.CleanUpCharactersAsync(Path.Combine(t.Root, "another", "_forever_"), [G]));
        Assert.All(gone, f => Assert.True(File.Exists(f), f));
    }

    [Fact]
    public async Task A_file_that_cannot_be_deleted_keeps_its_owner_so_the_character_is_offered_again()
    {
        // Codex on #78: a read-only cutout whose sidecar went first became nobody's portrait.
        using var t = new TempInstall();
        var (_, _, gone, _) = Files(t);
        t.WriteStore("1#1", Store());
        File.SetAttributes(gone[0], FileAttributes.ReadOnly);                  // the capture's TGA
        using var c = ArmoryWorkerTests.Started(t, new FakeCdn(), armory: false);
        ArmoryWorkerTests.Until(() => c.Current.Shell.Report?.Portraits?.Any(r => r.Guid == G && r.Gone) == true, "the gone row");
        var said = await c.CleanUpCharactersAsync(t.Install.FlavorDir, [G]);
        Assert.Contains("1 could not be deleted (karuzo-macphisto.tga)", said);
        Assert.True(File.Exists(gone[0]));
        Assert.True(File.Exists(gone[1]));                                      // its sidecar: still says whose it is
        Assert.False(File.Exists(gone[2]));                                     // its screenshots went
        Assert.Contains(c.Current.Shell.Report!.Portraits!, r => r.Guid == G && r.Gone);
        // The flag cleared, asked again: the rest goes.
        File.SetAttributes(gone[0], FileAttributes.Normal);
        Assert.Equal("Deleted 2 files of 1 character.", await c.CleanUpCharactersAsync(t.Install.FlavorDir, [G]));
        Assert.All(gone, f => Assert.False(File.Exists(f), f));
    }

    [Fact]
    public async Task A_screenshot_that_cannot_be_deleted_keeps_the_portraits_so_the_character_is_offered_again()
    {
        // Codex on #78: the screenshot failed, the portrait went anyway, and the character with
        // its leftovers left the list for good.
        using var t = new TempInstall();
        var (_, _, gone, _) = Files(t);
        t.WriteStore("1#1", Store());
        File.SetAttributes(gone[2], FileAttributes.ReadOnly);                  // the capture's first screenshot
        using var c = ArmoryWorkerTests.Started(t, new FakeCdn(), armory: false);
        ArmoryWorkerTests.Until(() => c.Current.Shell.Report?.Portraits?.Any(r => r.Guid == G && r.Gone) == true, "the gone row");
        Assert.Contains("1 could not be deleted (WoWScrnShot_102126_205809.tga)", await c.CleanUpCharactersAsync(t.Install.FlavorDir, [G]));
        Assert.True(File.Exists(gone[2]));
        Assert.False(File.Exists(gone[3]));                                     // the other screenshot went
        Assert.All(gone[..2].Concat(gone[4..6]), f => Assert.True(File.Exists(f), f));   // the portraits and their sidecars stay
        Assert.All(gone[6..], f => Assert.False(File.Exists(f), f));            // the enhanced picture, the history, the original went
        // Still in the list, offered again.
        Assert.Contains(c.Current.Shell.Report!.Portraits!, r => r.Guid == G && r.Gone && r.Ready);
        File.SetAttributes(gone[2], FileAttributes.Normal);
        Assert.Equal("Deleted 5 files of 1 character.", await c.CleanUpCharactersAsync(t.Install.FlavorDir, [G]));
        Assert.All(gone, f => Assert.False(File.Exists(f), f));
        Assert.DoesNotContain(c.Current.Shell.Report!.Portraits!, r => r.Guid == G);
    }

    [Fact]
    public void A_gone_character_with_only_files_left_and_no_portrait_is_still_offered()
    {
        // What a clean-up cut short leaves: no portrait, a file of it all the same.
        var captures = new[] { Status(A, "Karuzo Macphisto", CharacterState.Portrait), Status(G, "Karuzo Macphisto", CharacterState.Missing) };
        var owned = new HashSet<string>(StringComparer.Ordinal) { G, "Player-6796-00000001" };
        var rows = Collection.Build([Entry("karuzo-macphisto-0a598a", A)], captures, new Dictionary<string, DateTime>(), roster: Roster, owned: owned);
        var g = rows.Single(r => r.Guid == G);
        Assert.True(g.Gone && !g.Ready);
        // Files and nothing else: no capture record, no portrait - a row all the same.
        var unknown = rows.Single(r => r.Guid == "Player-6796-00000001");
        Assert.True(unknown.Gone);
        Assert.Equal(Collection.UnknownName, unknown.Name);
        Assert.Equal("2 characters AltStable no longer has", PassText.GoneLine(rows));
        Assert.Contains("Unknown character · Player-6796-00000001", PassText.CleanUpLines(rows));
        // A GUID AltStable has is never one, files or not.
        Assert.DoesNotContain(Collection.Build([], [], new Dictionary<string, DateTime>(), roster: Roster, owned: new HashSet<string> { A }), r => r.Gone);
    }

    [Fact]
    public async Task A_portrait_s_sidecar_that_cannot_be_deleted_keeps_the_character_offered()
    {
        // Codex on #78 (P3): the portrait went, its read-only sidecar could not, and nothing
        // left the character a row to retry from.
        using var t = new TempInstall();
        var (folder, _, gone, _) = Files(t);
        t.WriteStore("1#1", Store());
        File.SetAttributes(gone[1], FileAttributes.ReadOnly);                  // the capture's sidecar
        using var c = ArmoryWorkerTests.Started(t, new FakeCdn(), armory: false);
        ArmoryWorkerTests.Until(() => c.Current.Shell.Report?.Portraits?.Any(r => r.Guid == G && r.Gone) == true, "the gone row");
        Assert.Contains("1 could not be deleted (karuzo-macphisto.json)", await c.CleanUpCharactersAsync(t.Install.FlavorDir, [G]));
        Assert.True(File.Exists(gone[1]));
        Assert.False(File.Exists(gone[0]));
        Assert.Contains(G, Leftovers.OwnedGuids(folder));
        Assert.Contains(c.Current.Shell.Report!.Portraits!, r => r.Guid == G && r.Gone && !r.Ready);
        // The next pass keeps it too.
        c.ConvertNow();
        Thread.Sleep(2000);
        Assert.Contains(c.Current.Shell.Report!.Portraits!, r => r.Guid == G && r.Gone);
        File.SetAttributes(gone[1], FileAttributes.Normal);
        Assert.Equal("Deleted 1 file of 1 character.", await c.CleanUpCharactersAsync(t.Install.FlavorDir, [G]));
        Assert.All(gone, f => Assert.False(File.Exists(f), f));
        Assert.DoesNotContain(c.Current.Shell.Report!.Portraits!, r => r.Guid == G);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Files_of_a_character_with_no_capture_record_and_no_portrait_reach_the_list_and_the_clean_up(bool armory)
    {
        // Codex on #80: AltStable#344 drops a forgotten character's records; a clean-up cut short
        // leaves a sidecar, an enhanced picture, a history. The row is "Unknown character", and
        // the armory's own row filter, on or off, must not take it.
        using var t = new TempInstall();
        var (_, _, gone, _) = Files(t);
        foreach (var f in new[] { gone[0], gone[4], gone[5] }) File.Delete(f);   // no capture TGA, no armory render
        t.WriteStore("1#1", Store(gRecords: false));
        using var c = ArmoryWorkerTests.Started(t, new FakeCdn(), armory: armory);
        ArmoryWorkerTests.Until(() => c.Current.Shell.Report?.Portraits?.Any(r => r.Guid == G && r.Gone) == true, "the files-only row");
        Assert.Equal(Collection.UnknownName, c.Current.Shell.Report!.Portraits!.Single(r => r.Guid == G).Name);
        // A pass and a refresh later, still there.
        c.ConvertNow();
        Thread.Sleep(2000);
        Assert.Contains(c.Current.Shell.Report!.Portraits!, r => r.Guid == G && r.Gone);
        Assert.Equal("1 character AltStable no longer has", PassText.GoneLine(c.Current.Shell.Report.Portraits!));
        Assert.Equal("Deleted 7 files of 1 character.", await c.CleanUpCharactersAsync(t.Install.FlavorDir, [G]));
        Assert.All(gone, f => Assert.False(File.Exists(f), f));
        Assert.DoesNotContain(c.Current.Shell.Report!.Portraits!, r => r.Guid == G);
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
