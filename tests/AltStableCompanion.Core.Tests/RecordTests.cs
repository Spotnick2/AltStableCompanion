using AltStableCompanion.Core;

namespace AltStableCompanion.Core.Tests;

public class SavedVariablesReaderTests
{
    private static readonly DateTime T0 = new(2026, 9, 29, 0, 43, 4);

    [Fact]
    public void Records_are_read_from_behind_the_character_data_not_from_a_decoy()
    {
        var text = TestData.SavedVariables([
            TestData.Record("Kaleid Sumner", "Player-4618-006B8614", 1, T0),
            TestData.Record("Kaleid Sumner", "Player-4618-006B8614", 2, T0.AddSeconds(1)),
        ]);
        var store = SavedVariablesReader.Parse(text, "x")!;
        Assert.Equal(1, store.Version);
        Assert.Equal(2, store.Renders.Count);
        Assert.All(store.Renders, r => Assert.Equal("Kaleid Sumner", r.Name));
        Assert.Equal([1, 2], store.Renders.Select(r => r.Shot));
        Assert.Equal(T0, store.Renders[0].Stamp);
        Assert.Equal(TestData.Epoch(T0), store.Renders[0].Epoch);
        Assert.Equal(2160, store.Renders[0].ScreenH);
    }

    [Fact]
    public void The_nil_form_the_client_writes_means_no_captures()
    {
        // MEASURED on 1.60.1.70009: an account that never captured ends with this line.
        var text = "AltStableDB = {\n[\"renders\"] = { { [\"guid\"] = \"gx\", [\"shot\"] = 1, [\"stamp\"] = \"2020-01-01 00:00:00\" } },\n}\nAltStablePortraits = nil\n";
        Assert.Null(SavedVariablesReader.Parse(text, "x"));
    }

    [Fact]
    public void A_file_from_before_the_store_existed_means_no_captures() =>
        Assert.Null(SavedVariablesReader.Parse("AltStableDB = {\n}\n", "x"));

    [Fact]
    public void A_store_of_an_unknown_version_is_refused_not_guessed_at()
    {
        var text = TestData.SavedVariables([TestData.Record("A", "g", 1, T0)], version: 2);
        var store = SavedVariablesReader.Parse(text, "x")!;
        Assert.NotNull(store.Refused);
        Assert.Contains("update", store.Refused);
        Assert.Empty(store.Renders);
    }

    [Fact]
    public void A_file_cut_off_mid_table_is_a_format_error_to_retry()
    {
        var text = TestData.SavedVariables([TestData.Record("A", "g", 1, T0)]);
        Assert.Throws<SavedVariablesFormatException>(() => SavedVariablesReader.Parse(text[..(text.Length - 12)], "x"));
    }

    [Fact]
    public void A_record_missing_what_pairing_needs_is_dropped()
    {
        var bad = "{\n[\"name\"] = \"No Guid\",\n[\"shot\"] = 1,\n[\"stamp\"] = \"2026-09-29 00:43:04\",\n}";
        var store = SavedVariablesReader.Parse(TestData.SavedVariables([bad, TestData.Record("A", "g", 1, T0)]), "x")!;
        Assert.Single(store.Renders);
    }

    [Fact]
    public void Every_account_is_found_and_only_its_account_wide_store()
    {
        using var t = new TempInstall();
        t.WriteStore("1#1", "AltStablePortraits = nil\n");
        t.WriteStore("1#12", TestData.SavedVariables([TestData.Record("A", "g", 1, T0)]));
        // The unrelated folder WoW keeps beside the accounts.
        Directory.CreateDirectory(Path.Combine(t.Install.AccountsDir, "SavedVariables"));
        var found = SavedVariablesReader.FindStores(t.Install.AccountsDir).ToList();
        Assert.Equal(2, found.Count);
        Assert.Single(SavedVariablesReader.ReadAll(t.Install.AccountsDir));   // nil is not a store
    }
}

public class LuaTableScannerTests
{
    [Fact]
    public void Wow_formatting_parses()
    {
        var v = LuaTableScanner.ReadGlobal("""
            X = {
            ["a"] = "s\"q\\",
            ["n"] = -1.5e2,
            ["t"] = true,
            [3] = false,
            bare = 7,
            {
            1, 2,
            }, -- [1]
            }
            """, "X", out var found) as Dictionary<object, object?>;
        Assert.True(found);
        Assert.Equal("s\"q\\", v!["a"]);
        Assert.Equal(-150.0, v["n"]);
        Assert.Equal(true, v["t"]);
        Assert.Equal(false, v[3L]);
        Assert.Equal(7.0, v["bare"]);
        Assert.IsType<Dictionary<object, object?>>(v[1L]);
    }

    [Fact]
    public void Only_an_assignment_at_the_start_of_a_line_counts()
    {
        Assert.Null(LuaTableScanner.ReadGlobal("-- X = { } in a comment\nY = 1\n", "X", out var found));
        Assert.False(found);
    }
}

public class SluggerTests
{
    [Theory]
    [InlineData("Kaleid Sumner", "kaleid-sumner")]
    [InlineData("Morphisto Ruskador", "morphisto-ruskador")]
    [InlineData("  O'Brien--the  Bold ", "o-brien-the-bold")]
    [InlineData("Zoë Ångström", "zo-ngstr-m")]
    public void Slugs_match_the_roster_and_the_python_converter(string name, string want) =>
        Assert.Equal(want, Slugger.Slug(name));

    [Fact]
    public void A_namesake_gets_the_end_of_its_guid()
    {
        Assert.Equal("twin-name", Slugger.FileBase("Twin Name", "Player-1-AAAAAAAA", _ => null));
        Assert.Equal("twin-name", Slugger.FileBase("Twin Name", "Player-1-AAAAAAAA", _ => "Player-1-AAAAAAAA"));
        Assert.Equal("twin-name-bbbbbb", Slugger.FileBase("Twin Name", "Player-2-BBBBBBBB", _ => "Player-1-AAAAAAAA"));
        // Uppercase hex survives: make-cutout.py strips [^A-Za-z0-9] and lowercases after.
        Assert.Equal("kaleid-sumner-6b8614", Slugger.FileBase("Kaleid Sumner", "Player-4618-006B8614", _ => "other"));
    }
}

public class CapturePairingTests
{
    private static readonly DateTime T0 = new(2026, 9, 29, 1, 6, 38);

    private static PortraitStore Store(string path, params (string Name, string Guid, int Shot, DateTime When, long? Epoch)[] rs) =>
        new(path, 1, [.. rs.Select(r => new RenderRecord(r.Guid, r.Name, r.Shot, r.When, r.Epoch ?? TestData.Epoch(r.When), null, 2160, null, null, path))]);

    [Fact]
    public void Shots_pair_per_store_never_across_accounts()
    {
        var caps = CapturePairing.Pair([
            Store("a", ("A", "g1", 1, T0, null)),
            Store("b", ("A", "g1", 2, T0.AddSeconds(1), null)),
        ]);
        Assert.Empty(caps);
    }

    [Fact]
    public void A_lone_first_shot_is_an_abandoned_capture()
    {
        var caps = CapturePairing.Pair([Store("a",
            ("A", "g1", 1, T0, null),
            ("A", "g1", 1, T0.AddSeconds(10), null),
            ("A", "g1", 2, T0.AddSeconds(11), null))]);
        Assert.Single(caps);
        Assert.Equal(T0.AddSeconds(10), caps[0].First);
    }

    [Fact]
    public void Newest_is_by_epoch_not_by_the_local_stamp_that_repeats_at_dst()
    {
        // 01:40 before the clocks go back, then 01:20 AFTER - a later epoch, an earlier stamp.
        var before = new DateTime(2026, 10, 25, 1, 40, 0);
        var after = new DateTime(2026, 10, 25, 1, 20, 0);
        var caps = CapturePairing.Pair([Store("a",
            ("A", "g1", 1, before, 1792888800), ("A", "g1", 2, before.AddSeconds(1), 1792888801),
            ("A", "g1", 1, after, 1792890000), ("A", "g1", 2, after.AddSeconds(1), 1792890001))]);
        Assert.Equal(after, CapturePairing.NewestPerGuid(caps).Single().First);
    }

    [Fact]
    public void Two_characters_sharing_a_name_are_two_characters()
    {
        var caps = CapturePairing.Pair([Store("a",
            ("Twin", "g1", 1, T0, null), ("Twin", "g1", 2, T0.AddSeconds(1), null),
            ("Twin", "g2", 1, T0.AddSeconds(5), null), ("Twin", "g2", 2, T0.AddSeconds(6), null))]);
        Assert.Equal(2, CapturePairing.NewestPerGuid(caps).Count);
    }

    private static Capture Cap(DateTime first, string guid = "g1") =>
        new("A", guid, first, first.AddSeconds(1), TestData.Epoch(first), 2160, "a");

    private static Dictionary<string, DateTime> Files(params DateTime[] at) =>
        at.ToDictionary(t => TestData.ShotName(t), t => t);

    [Fact]
    public void A_clean_pair_matches_its_own_two_files()
    {
        var a = CapturePairing.Assign([Cap(T0)], Files(T0, T0.AddSeconds(1))).Single();
        Assert.Equal(MatchProblem.None, a.Problem);
        Assert.Equal(TestData.ShotName(T0), a.Black);
        Assert.Equal(TestData.ShotName(T0.AddSeconds(1)), a.White);
    }

    [Fact]
    public void A_failed_shutter_does_not_borrow_a_hand_taken_screenshot()
    {
        // Shot 1 never reached disk; the player took their own screenshot 3 seconds later.
        // The Python converter paired shot 2's file as black and the hand-taken one as white.
        var a = CapturePairing.Assign([Cap(T0)], Files(T0.AddSeconds(1), T0.AddSeconds(4))).Single();
        Assert.NotEqual(MatchProblem.None, a.Problem);
    }

    [Fact]
    public void A_file_two_captures_want_belongs_to_neither()
    {
        // Two accounts capturing in the same seconds.
        var result = CapturePairing.Assign([Cap(T0, "g1"), Cap(T0, "g2")], Files(T0, T0.AddSeconds(1)));
        Assert.All(result, a => Assert.Equal(MatchProblem.Ambiguous, a.Problem));
    }

    [Fact]
    public void Captures_seconds_apart_on_two_accounts_each_get_their_own()
    {
        var result = CapturePairing.Assign([Cap(T0, "g1"), Cap(T0.AddSeconds(2), "g2")],
            Files(T0, T0.AddSeconds(1), T0.AddSeconds(2), T0.AddSeconds(3)));
        Assert.All(result, a => Assert.Equal(MatchProblem.None, a.Problem));
    }

    [Fact]
    public void Both_shots_in_one_second_is_a_collision()
    {
        var cap = new Capture("A", "g1", T0, T0, TestData.Epoch(T0), 2160, "a");
        Assert.Equal(MatchProblem.Collided, CapturePairing.Assign([cap], Files(T0)).Single().Problem);
    }

    [Fact]
    public void Nothing_on_disk_is_missing() =>
        Assert.Equal(MatchProblem.Missing, CapturePairing.Assign([Cap(T0)], Files()).Single().Problem);

    [Fact]
    public void Screenshot_names_carry_their_own_time()
    {
        using var t = new TempInstall();
        File.WriteAllBytes(Path.Combine(t.Install.Screenshots, "WoWScrnShot_092926_010638.tga"), []);
        File.WriteAllBytes(Path.Combine(t.Install.Screenshots, "WoWScrnShot_092926_010638.jpg"), []);
        File.WriteAllBytes(Path.Combine(t.Install.Screenshots, "holiday.tga"), []);
        var times = CapturePairing.ShotTimes(t.Install.Screenshots);
        Assert.Equal(T0, times.Single().Value);
    }

    [Fact]
    public void Screenshots_ahead_of_the_records_mean_the_client_has_not_saved_yet()
    {
        var stores = new[] { Store("a", ("A", "g1", 1, T0, null)) };
        Assert.Null(CapturePairing.StoreIsStale(stores, Files(T0.AddSeconds(30))));
        Assert.NotNull(CapturePairing.StoreIsStale(stores, Files(T0.AddMinutes(5))));
        Assert.Equal((T0, null), CapturePairing.StoreIsStale([], Files(T0)));
    }
}
