using AltStableCompanion.Core;

namespace AltStableCompanion.Core.Tests;

public class CutoutFolderTests
{
    [Fact]
    public void The_toc_is_written_once_and_the_folder_reported_as_new_once()
    {
        using var t = new TempInstall();
        var f = new CutoutFolder(t.Install.CutoutAddonDir);
        Assert.True(f.EnsureToc());
        var written = File.GetLastWriteTimeUtc(f.TocPath);
        Assert.False(f.EnsureToc());
        Assert.Equal(written, File.GetLastWriteTimeUtc(f.TocPath));
        Assert.Contains("## Interface: 16001", File.ReadAllText(f.TocPath));
        Assert.Contains("## Dependencies: AltStable", File.ReadAllText(f.TocPath));
    }

    [Fact]
    public void A_toc_that_differs_is_rewritten()
    {
        using var t = new TempInstall();
        var f = new CutoutFolder(t.Install.CutoutAddonDir);
        f.EnsureToc();
        File.WriteAllText(f.TocPath, "## Interface: 11500\n");
        Assert.False(f.EnsureToc());
        Assert.Contains("16001", File.ReadAllText(f.TocPath));
    }

    [Fact]
    public void The_manifest_keys_guid_entries_by_guid_and_legacy_ones_by_name()
    {
        using var t = new TempInstall();
        var f = new CutoutFolder(t.Install.CutoutAddonDir);
        f.EnsureToc();
        f.WriteCutout("kaleid-sumner", TestData.Solid(4, 4, 1, 1, 1),
            new CutoutMeta { W = 3, H = 4, TexW = 4, TexH = 4, NativeUnit = "screen", NativeW = 0.20509, NativeH = 0.63472, Guid = "Player-4618-006B8614" });
        // A cutout from before sidecars existed: sizes come from the image.
        var legacy = new RgbaImage(8, 8);
        legacy[1, 1] = (1, 1, 1, 255);
        legacy[2, 4] = (1, 1, 1, 255);
        TgaCodec.Write(Path.Combine(f.CutoutsDir, "zoruka-sumner.tga"), legacy);
        // A sidecar in raw pixels (no unit): kept, but without a height.
        f.WriteCutout("old-pixels", TestData.Solid(4, 4, 1, 1, 1),
            new CutoutMeta { W = 3, H = 4, TexW = 4, TexH = 4, NativeW = 100, NativeH = 600 });

        var lua = ManifestWriter.Render(f.Inventory(), new DateTime(2026, 9, 29, 1, 7, 30));
        Assert.Contains("-- Generated 2026-09-29 01:07:30", lua);
        Assert.Contains(@"    ['Player-4618-006B8614'] = { guid = 'Player-4618-006B8614', file = [[Interface\AddOns\AltStableCutouts\Cutouts\kaleid-sumner.tga]], w = 3, h = 4, texw = 4, texh = 4, nativeW = 0.20509, nativeH = 0.63472 },", lua);
        Assert.Contains(@"    ['zoruka-sumner'] = { file = [[Interface\AddOns\AltStableCutouts\Cutouts\zoruka-sumner.tga]], w = 2, h = 4, texw = 8, texh = 8 },", lua);
        Assert.Contains(@"    ['old-pixels'] = { file = [[Interface\AddOns\AltStableCutouts\Cutouts\old-pixels.tga]], w = 3, h = 4, texw = 4, texh = 4 },", lua);
        Assert.StartsWith("-- CutoutManifest.lua", lua);
        Assert.Contains("AltStableCutoutManifest = {", lua);
        Assert.DoesNotContain("\n\n\n", lua.ReplaceLineEndings("\n"));
    }

    [Fact]
    public void A_folder_without_a_toc_is_an_addon_the_client_has_not_seen()
    {
        // A first run cut short, or a .toc deleted by hand: the folder is there, the addon is not.
        using var t = new TempInstall();
        var f = new CutoutFolder(t.Install.CutoutAddonDir);
        Directory.CreateDirectory(f.CutoutsDir);
        Assert.True(f.EnsureToc());
        Assert.False(f.EnsureToc());
    }

    [Fact]
    public void A_character_with_two_files_is_listed_once_by_its_newest()
    {
        // make-cutout.py named files by the current name: a renamed character leaves two.
        using var t = new TempInstall();
        var f = new CutoutFolder(t.Install.CutoutAddonDir);
        var meta = new CutoutMeta { W = 3, H = 4, TexW = 4, TexH = 4, Guid = "Player-1-AAAA" };
        f.WriteCutout("kaleid", TestData.Solid(4, 4, 1, 1, 1), meta);
        f.WriteCutout("kaleid-sumner", TestData.Solid(4, 4, 1, 1, 1), meta with { Epoch = 100 });
        f.WriteCutout("zz-older", TestData.Solid(4, 4, 1, 1, 1), meta with { Epoch = 50 });

        Assert.Equal("kaleid-sumner", f.FileBaseOf("Player-1-AAAA"));
        var warnings = new List<string>();
        var entry = Assert.Single(f.Inventory(warnings.Add));
        Assert.Equal("kaleid-sumner.tga", entry.FileName);
        Assert.Equal(2, warnings.Count);
    }

    [Fact]
    public void A_name_that_would_break_the_manifest_is_left_out_of_it()
    {
        using var t = new TempInstall();
        var f = new CutoutFolder(t.Install.CutoutAddonDir);
        f.EnsureToc();
        TgaCodec.Write(Path.Combine(f.CutoutsDir, "o'brien.tga"), TestData.Solid(4, 4, 1, 1, 1));
        TgaCodec.Write(Path.Combine(f.CutoutsDir, "a]]b.tga"), TestData.Solid(4, 4, 1, 1, 1));
        TgaCodec.Write(Path.Combine(f.CutoutsDir, "fine.tga"), TestData.Solid(4, 4, 1, 1, 1));
        f.WriteCutout("tampered", TestData.Solid(4, 4, 1, 1, 1),
            new CutoutMeta { W = 3, H = 4, TexW = 4, TexH = 4, Guid = "a'] = 1, evil = { ['b" });

        var warnings = new List<string>();
        Assert.Equal("fine.tga", Assert.Single(f.Inventory(warnings.Add)).FileName);
        Assert.Equal(3, warnings.Count);

        // And the writer holds the line by itself, whatever it is handed.
        var lua = ManifestWriter.Render([
            new ManifestEntry("o'brien", null, "o'brien.tga", 1, 1, 1, 1, null, null),
            new ManifestEntry("g", "a'] = 1, evil = { ['b", "g.tga", 1, 1, 1, 1, null, null),
            new ManifestEntry("ok", null, "a]]b.tga", 1, 1, 1, 1, null, null),
            new ManifestEntry("fine", null, "fine.tga", 1, 1, 1, 1, null, null),
        ], DateTime.Now);
        Assert.DoesNotContain("brien", lua);
        Assert.DoesNotContain("evil", lua);
        Assert.DoesNotContain("]]b", lua);
        Assert.Contains("['fine']", lua);
    }

    [Fact]
    public void A_manifest_whose_entries_did_not_change_is_not_written_again()
    {
        using var t = new TempInstall();
        var f = new CutoutFolder(t.Install.CutoutAddonDir);
        f.EnsureToc();
        f.WriteCutout("one", TestData.Solid(4, 4, 1, 1, 1), new CutoutMeta { W = 3, H = 4, TexW = 4, TexH = 4 });
        Assert.True(f.WriteManifest(new DateTime(2026, 9, 29, 1, 0, 0)));
        Assert.False(f.WriteManifest(new DateTime(2026, 9, 29, 2, 0, 0)));
        Assert.Contains("-- Generated 2026-09-29 01:00:00", File.ReadAllText(f.ManifestPath));

        f.WriteCutout("two", TestData.Solid(4, 4, 1, 1, 1), new CutoutMeta { W = 3, H = 4, TexW = 4, TexH = 4 });
        Assert.True(f.WriteManifest(new DateTime(2026, 9, 29, 3, 0, 0)));
        Assert.Contains("['two']", File.ReadAllText(f.ManifestPath));
    }

    [Fact]
    public void A_file_the_manifest_will_not_list_holds_nobody_s_portrait()
    {
        // Newer, and under a name that cannot go into the manifest. It used to win: the capture
        // read as converted while the game was given nothing to draw.
        using var t = new TempInstall();
        var f = new CutoutFolder(t.Install.CutoutAddonDir);
        var meta = new CutoutMeta { W = 3, H = 4, TexW = 4, TexH = 4, Guid = "Player-1-AAAA" };
        f.WriteCutout("kaleid", TestData.Solid(4, 4, 1, 1, 1), meta with { Epoch = 50 });
        f.WriteCutout("kaleid's new", TestData.Solid(4, 4, 1, 1, 1), meta with { Epoch = 100 });

        Assert.Equal("kaleid", f.FileBaseOf("Player-1-AAAA"));
        Assert.Equal("kaleid.tga", Assert.Single(f.Inventory()).FileName);
    }

    [Fact]
    public void A_sidecar_the_python_converter_wrote_is_read()
    {
        using var t = new TempInstall();
        var f = new CutoutFolder(t.Install.CutoutAddonDir);
        Directory.CreateDirectory(f.CutoutsDir);
        File.WriteAllText(Path.Combine(f.CutoutsDir, "karuzo-sumner.json"), """
            {
              "w": 165, "h": 512, "texw": 256, "texh": 512,
              "nativeW": 0.20509, "nativeH": 0.63472, "nativeUnit": "screen",
              "nativePx": [443, 1371], "guid": "Player-1-X"
            }
            """);
        var m = f.ReadMeta("karuzo-sumner")!;
        Assert.Equal((165, 512, 256, 512), (m.W, m.H, m.TexW, m.TexH));
        Assert.Equal("Player-1-X", m.Guid);
        Assert.Null(m.Epoch);
    }
}

public class ConvertPassTests
{
    private static readonly DateTime T0 = new(2026, 9, 29, 1, 6, 38);
    private const string Guid1 = "Player-4618-006B8614";

    private static void Capture(TempInstall t, string account, string name, string guid, DateTime at)
    {
        var (b, w) = TestData.Pair(400, 1200, 150, 250, 100, 700);
        t.WriteShot(at, b);
        t.WriteShot(at.AddSeconds(1), w);
    }

    private static string Records(params (string Name, string Guid, DateTime At)[] caps) =>
        TestData.SavedVariables(caps.SelectMany(c => new[]
        {
            TestData.Record(c.Name, c.Guid, 1, c.At),
            TestData.Record(c.Name, c.Guid, 2, c.At.AddSeconds(1)),
        }));

    [Fact]
    public void A_capture_becomes_a_cutout_a_sidecar_and_a_manifest_entry()
    {
        using var t = new TempInstall();
        Capture(t, "1#12", "Kaleid Sumner", Guid1, T0);
        t.WriteStore("1#12", Records(("Kaleid Sumner", Guid1, T0)));
        var hand = Path.Combine(t.Install.Screenshots, TestData.ShotName(T0.AddMinutes(-10)));
        File.WriteAllBytes(hand, TestData.WowScreenshot(TestData.Solid(4, 4, 9, 9, 9)));

        var report = new ConvertPass(t.Install, new ConvertOptions()).Run();

        Assert.Equal(["Kaleid Sumner"], report.Written);
        Assert.True(report.FolderCreated);
        var folder = new CutoutFolder(t.Install.CutoutAddonDir);
        Assert.True(File.Exists(Path.Combine(folder.CutoutsDir, "kaleid-sumner.tga")));
        var meta = folder.ReadMeta("kaleid-sumner")!;
        Assert.Equal(Guid1, meta.Guid);
        Assert.Equal(TestData.Epoch(T0), meta.Epoch);
        Assert.Contains($"['{Guid1}'] = {{ guid = '{Guid1}'", File.ReadAllText(folder.ManifestPath));
        Assert.Equal(CharacterState.Portrait, report.Characters.Single().State);

        // The two screenshots it consumed are gone; the hand-taken one is untouched.
        Assert.False(File.Exists(Path.Combine(t.Install.Screenshots, TestData.ShotName(T0))));
        Assert.False(File.Exists(Path.Combine(t.Install.Screenshots, TestData.ShotName(T0.AddSeconds(1)))));
        Assert.True(File.Exists(hand));
        Assert.True(report.BytesFreed > 0);
    }

    [Fact]
    public void A_second_pass_converts_nothing_and_reports_the_portrait()
    {
        using var t = new TempInstall();
        Capture(t, "1#12", "Kaleid Sumner", Guid1, T0);
        t.WriteStore("1#12", Records(("Kaleid Sumner", Guid1, T0)));
        var pass = new ConvertPass(t.Install, new ConvertOptions(KeepScreenshots: true));
        pass.Run();

        var again = pass.Run();
        Assert.Empty(again.Written);
        Assert.False(again.FolderCreated);
        Assert.Equal(CharacterState.Portrait, again.Characters.Single().State);
        // Kept, as asked, and still there after the second pass.
        Assert.True(File.Exists(Path.Combine(t.Install.Screenshots, TestData.ShotName(T0))));
    }

    [Fact]
    public void A_newer_capture_replaces_the_portrait_in_the_same_file()
    {
        using var t = new TempInstall();
        Capture(t, "1#12", "Kaleid Sumner", Guid1, T0);
        t.WriteStore("1#12", Records(("Kaleid Sumner", Guid1, T0)));
        var pass = new ConvertPass(t.Install, new ConvertOptions());
        pass.Run();

        var later = T0.AddMinutes(2);
        Capture(t, "1#12", "Kaleid Sumner", Guid1, later);
        t.WriteStore("1#12", Records(("Kaleid Sumner", Guid1, T0), ("Kaleid Sumner", Guid1, later)));
        var report = pass.Run();

        Assert.Equal(["Kaleid Sumner"], report.Written);
        var folder = new CutoutFolder(t.Install.CutoutAddonDir);
        Assert.Equal(TestData.Epoch(later), folder.ReadMeta("kaleid-sumner")!.Epoch);
        Assert.Single(Directory.EnumerateFiles(folder.CutoutsDir, "*.tga"));
    }

    [Fact]
    public void Two_accounts_and_two_namesakes_each_get_their_own_portrait()
    {
        using var t = new TempInstall();
        Capture(t, "1#1", "Twin Name", "Player-1-AAAAAAAA", T0);
        Capture(t, "1#12", "Twin Name", "Player-2-BBBBBBBB", T0.AddSeconds(5));
        t.WriteStore("1#1", Records(("Twin Name", "Player-1-AAAAAAAA", T0)));
        t.WriteStore("1#12", Records(("Twin Name", "Player-2-BBBBBBBB", T0.AddSeconds(5))));

        var report = new ConvertPass(t.Install, new ConvertOptions()).Run();

        Assert.Equal(2, report.Written.Count);
        var tgas = Directory.EnumerateFiles(new CutoutFolder(t.Install.CutoutAddonDir).CutoutsDir, "*.tga")
            .Select(Path.GetFileNameWithoutExtension).Order().ToList();
        Assert.Equal(["twin-name", "twin-name-bbbbbb"], tgas);
    }

    [Fact]
    public void Three_namesakes_whose_guids_end_alike_keep_three_portraits()
    {
        using var t = new TempInstall();
        var who = new[] { "Player-1-AAAAAAAA", "Player-2-00BBBBBB", "Player-3-00BBBBBB" };
        for (var i = 0; i < who.Length; i++) Capture(t, "1#12", "Twin", who[i], T0.AddMinutes(i));
        t.WriteStore("1#12", Records([.. who.Select((g, i) => ("Twin", g, T0.AddMinutes(i)))]));

        var report = new ConvertPass(t.Install, new ConvertOptions()).Run();

        Assert.Equal(3, report.Written.Count);
        var folder = new CutoutFolder(t.Install.CutoutAddonDir);
        Assert.Equal(who, folder.Inventory().Select(e => e.Guid).Order());
        Assert.Equal(["twin", "twin-bbbbbb", "twin-player-3-00bbbbbb"],
            Directory.EnumerateFiles(folder.CutoutsDir, "*.tga").Select(Path.GetFileNameWithoutExtension).Order());
    }

    [Fact]
    public void A_kept_pair_is_not_lent_to_a_capture_whose_own_shots_are_missing()
    {
        // A is converted and its screenshots kept. Then another account's records turn up: B,
        // two seconds EARLIER, its own screenshots never written. A's pair is two seconds from
        // B's stamps and rightly spaced - and is A's.
        using var t = new TempInstall();
        const string a = "Player-1-AAAAAAAA", b = "Player-2-BBBBBBBB";
        var atA = T0.AddSeconds(2);
        Capture(t, "1#1", "Aaa", a, atA);
        t.WriteStore("1#1", Records(("Aaa", a, atA)));
        new ConvertPass(t.Install, new ConvertOptions(KeepScreenshots: true)).Run();
        var folder = new CutoutFolder(t.Install.CutoutAddonDir);
        Assert.Equal([TestData.ShotName(atA), TestData.ShotName(atA.AddSeconds(1))], folder.ReadMeta("aaa")!.Shots!);

        t.WriteStore("1#12", Records(("Bbb", b, T0)));
        var report = new ConvertPass(t.Install, new ConvertOptions()).Run();

        Assert.Empty(report.Written);
        Assert.Equal(CharacterState.Ambiguous, report.Characters.Single(c => c.Guid == b).State);
        Assert.Null(folder.FileBaseOf(b));
        Assert.True(File.Exists(Path.Combine(t.Install.Screenshots, TestData.ShotName(atA))));
        Assert.True(File.Exists(Path.Combine(t.Install.Screenshots, TestData.ShotName(atA.AddSeconds(1)))));
    }

    [Fact]
    public void A_kept_pair_stays_its_cutout_s_when_its_character_has_captured_again()
    {
        // A is converted and its screenshots kept. A captures AGAIN, and that capture's
        // screenshots never reach disk: the cutout on disk is no longer of A's newest capture.
        // Then B's records turn up, two seconds before A's first pair, B's own shots missing.
        using var t = new TempInstall();
        const string a = "Player-1-AAAAAAAA", b = "Player-2-BBBBBBBB";
        var first = T0.AddSeconds(2);
        var again = T0.AddMinutes(1);
        Capture(t, "1#1", "Aaa", a, first);
        t.WriteStore("1#1", Records(("Aaa", a, first)));
        new ConvertPass(t.Install, new ConvertOptions(KeepScreenshots: true)).Run();

        t.WriteStore("1#1", Records(("Aaa", a, first), ("Aaa", a, again)));
        t.WriteStore("1#12", Records(("Bbb", b, T0)));
        var report = new ConvertPass(t.Install, new ConvertOptions()).Run();

        Assert.Empty(report.Written);
        Assert.Equal(CharacterState.Missing, report.Characters.Single(c => c.Guid == a).State);
        Assert.Equal(CharacterState.Ambiguous, report.Characters.Single(c => c.Guid == b).State);
        var folder = new CutoutFolder(t.Install.CutoutAddonDir);
        Assert.Null(folder.FileBaseOf(b));
        Assert.True(File.Exists(Path.Combine(t.Install.Screenshots, TestData.ShotName(first))));
        Assert.True(File.Exists(Path.Combine(t.Install.Screenshots, TestData.ShotName(first.AddSeconds(1)))));
    }

    [Fact]
    public void A_cutout_that_is_gone_has_no_claim_and_a_sidecar_cannot_name_a_path()
    {
        using var t = new TempInstall();
        var f = new CutoutFolder(t.Install.CutoutAddonDir);
        var meta = new CutoutMeta { W = 3, H = 4, TexW = 4, TexH = 4, Guid = "Player-1-AAAAAAAA", Epoch = 1 };
        f.WriteCutout("here", TestData.Solid(4, 4, 1, 1, 1), meta with { Shots = ["a.tga", @"..\..\elsewhere\b.tga", ""] });
        f.WriteCutout("gone", TestData.Solid(4, 4, 1, 1, 1), meta with { Shots = ["c.tga"] });
        File.Delete(Path.Combine(f.CutoutsDir, "gone.tga"));
        f.WriteCutout("no-shots", TestData.Solid(4, 4, 1, 1, 1), meta);

        Assert.Equal(["a.tga", "b.tga"], f.RecordedShots());
    }

    [Fact]
    public void A_screenshot_one_capture_may_still_want_is_not_deleted_by_another()
    {
        using var t = new TempInstall();
        var (black, white) = TestData.Pair(400, 1200, 150, 250, 100, 700);
        foreach (var s in new[] { 0, 3 }) t.WriteShot(T0.AddSeconds(s), black);
        foreach (var s in new[] { 2, 4 }) t.WriteShot(T0.AddSeconds(s), s == 2 ? black : white);
        static string Two(string name, string guid, DateTime first, DateTime second) => TestData.SavedVariables(
            [TestData.Record(name, guid, 1, first), TestData.Record(name, guid, 2, second)]);
        t.WriteStore("1#1", Two("Aaa", "Player-1-AAAAAAAA", T0.AddSeconds(1), T0.AddSeconds(3)));
        t.WriteStore("1#12", Two("Bbb", "Player-2-BBBBBBBB", T0.AddSeconds(2), T0.AddSeconds(4)));

        var report = new ConvertPass(t.Install, new ConvertOptions()).Run();

        Assert.Empty(report.Written);
        Assert.All(report.Characters, c => Assert.Equal(CharacterState.Ambiguous, c.State));
        Assert.Equal(4, Directory.EnumerateFiles(t.Install.Screenshots, "*.tga").Count());
    }

    [Fact]
    public void A_rejected_pair_stays_on_disk_and_is_not_decoded_again()
    {
        using var t = new TempInstall();
        var same = TestData.Solid(400, 1200, 90, 90, 90);        // identical shots
        t.WriteShot(T0, same);
        t.WriteShot(T0.AddSeconds(1), same);
        t.WriteStore("1#12", Records(("Kaleid Sumner", Guid1, T0)));
        var pass = new ConvertPass(t.Install, new ConvertOptions());

        var report = pass.Run();
        Assert.Equal(CharacterState.Rejected, report.Characters.Single().State);
        Assert.True(File.Exists(Path.Combine(t.Install.Screenshots, TestData.ShotName(T0))));

        // Make the file unreadable as a TGA WITHOUT changing its size or time: a second decode
        // would now fail differently, so the same "Rejected" proves nothing was decoded.
        var path = Path.Combine(t.Install.Screenshots, TestData.ShotName(T0));
        var stamp = File.GetLastWriteTimeUtc(path);
        var bytes = File.ReadAllBytes(path);
        bytes[2] = 99;
        File.WriteAllBytes(path, bytes);
        File.SetLastWriteTimeUtc(path, stamp);
        Assert.Equal(CharacterState.Rejected, pass.Run().Characters.Single().State);
    }

    [Fact]
    public void Records_not_yet_saved_are_reported_as_stale()
    {
        using var t = new TempInstall();
        t.WriteStore("1#12", Records(("Kaleid Sumner", Guid1, T0)));
        Capture(t, "1#12", "Kaleid Sumner", Guid1, T0.AddMinutes(10));   // taken, not yet saved
        var report = new ConvertPass(t.Install, new ConvertOptions()).Run();
        Assert.NotNull(report.Stale);
        Assert.Equal(CharacterState.Missing, report.Characters.Single().State);
    }

    [Fact]
    public void An_unsupported_store_is_reported_and_its_screenshots_left_alone()
    {
        using var t = new TempInstall();
        Capture(t, "1#12", "Kaleid Sumner", Guid1, T0);
        t.WriteStore("1#12", TestData.SavedVariables(
            [TestData.Record("Kaleid Sumner", Guid1, 1, T0), TestData.Record("Kaleid Sumner", Guid1, 2, T0.AddSeconds(1))],
            version: 2));
        var report = new ConvertPass(t.Install, new ConvertOptions()).Run();
        Assert.Empty(report.Written);
        Assert.Contains(report.Warnings, w => w.Contains("update"));
        Assert.True(File.Exists(Path.Combine(t.Install.Screenshots, TestData.ShotName(T0))));
    }

    [Fact]
    public void A_portrait_the_python_converter_made_is_a_portrait()
    {
        // Its sidecar has the guid and no epoch, and it deleted the screenshots it used.
        using var t = new TempInstall();
        var f = new CutoutFolder(t.Install.CutoutAddonDir);
        f.EnsureToc();
        f.WriteCutout("kaleid-sumner", TestData.Solid(4, 4, 1, 1, 1),
            new CutoutMeta { W = 3, H = 4, TexW = 4, TexH = 4, Guid = Guid1 });
        t.WriteStore("1#12", Records(("Kaleid Sumner", Guid1, T0)));

        var log = new List<string>();
        var pass = new ConvertPass(t.Install, new ConvertOptions(), log.Add);
        var status = pass.Run().Characters.Single();
        Assert.Equal(CharacterState.Portrait, status.State);
        // Which capture it came from is not known - and that is for the log, not for the player.
        Assert.True(status.Undated);
        Assert.Null(status.Note);
        Assert.False(status.NearlySquare);
        pass.Run();
        Assert.Single(log, l => l.Contains("earlier converter"));

        // What its sidecar does say - the shape - is not lost with the date.
        f.WriteCutout("kaleid-sumner", TestData.Solid(4, 4, 1, 1, 1),
            new CutoutMeta { W = 4, H = 4, TexW = 4, TexH = 4, Guid = Guid1, NativePx = [90, 100] });
        Assert.True(new ConvertPass(t.Install, new ConvertOptions()).Run().Characters.Single().NearlySquare);
    }

    [Fact]
    public async Task A_cutout_somebody_is_reading_is_still_replaced()
    {
        // The window reads a cutout for its thumbnail for a few milliseconds. Windows will not
        // replace a file that is open, so the writer waits it out.
        using var t = new TempInstall();
        var f = new CutoutFolder(t.Install.CutoutAddonDir);
        f.WriteCutout("kaleid-sumner", TestData.Solid(4, 4, 1, 1, 1), new CutoutMeta { W = 4, H = 4, TexW = 4, TexH = 4 });
        var path = Path.Combine(f.CutoutsDir, "kaleid-sumner.tga");
        using var reading = new ManualResetEventSlim();
        var reader = Task.Run(() =>
        {
            using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            reading.Set();
            Thread.Sleep(150);
        });
        reading.Wait();
        f.WriteCutout("kaleid-sumner", TestData.Solid(4, 4, 9, 9, 9), new CutoutMeta { W = 4, H = 4, TexW = 4, TexH = 4 });
        await reader;
        Assert.Equal(((byte)9, (byte)9, (byte)9, (byte)255), TgaCodec.Read(path)[0, 0]);
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void A_screenshot_in_use_is_missing_for_now_not_gone()
    {
        using var t = new TempInstall();
        var (b, w) = TestData.Pair(400, 1200, 100, 300, 70, 100);
        t.WriteShot(T0, b);
        t.WriteShot(T0.AddSeconds(1), w);
        t.WriteStore("1#12", Records(("Kaleid Sumner", Guid1, T0)));
        var pass = new ConvertPass(t.Install, new ConvertOptions());

        using (File.Open(Path.Combine(t.Install.Screenshots, TestData.ShotName(T0)), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var report = pass.Run();
            var held = report.Characters.Single();
            Assert.Equal(CharacterState.Missing, held.State);
            Assert.True(held.Transient);
            // The whole way to the headline: the app is waiting, and says so.
            Directory.CreateDirectory(Path.Combine(t.Install.AddOnsDir, "AltStable"));
            File.WriteAllText(Path.Combine(t.Install.AddOnsDir, "AltStable", "AltStable.toc"), "");
            var headline = PassText.Headline(new ShellState(Install: t.Install, Report: report));
            Assert.Equal("A capture is still being written", headline.Title);
            Assert.Equal(HeadlineKind.Info, headline.Kind);
        }
        Assert.Single(pass.Run().Written);
        var gone = pass.Run().Characters.Single();
        Assert.Equal(CharacterState.Portrait, gone.State);
        Assert.False(gone.Transient);
    }

    [Fact]
    public void A_portrait_the_python_converter_made_is_replaced_when_its_screenshots_are_there()
    {
        using var t = new TempInstall();
        var f = new CutoutFolder(t.Install.CutoutAddonDir);
        f.EnsureToc();
        f.WriteCutout("kaleid-sumner", TestData.Solid(4, 4, 1, 1, 1),
            new CutoutMeta { W = 3, H = 4, TexW = 4, TexH = 4, Guid = Guid1 });
        Capture(t, "1#12", "Kaleid Sumner", Guid1, T0);
        t.WriteStore("1#12", Records(("Kaleid Sumner", Guid1, T0)));

        var report = new ConvertPass(t.Install, new ConvertOptions()).Run();
        Assert.Equal(["Kaleid Sumner"], report.Written);
        Assert.Equal(TestData.Epoch(T0), f.ReadMeta("kaleid-sumner")!.Epoch);
    }

    [Fact]
    public void The_nearly_square_warning_outlives_the_pass_that_wrote_the_cutout()
    {
        using var t = new TempInstall();
        var (b, w) = TestData.Pair(400, 1200, 100, 300, 90, 100);
        t.WriteShot(T0, b);
        t.WriteShot(T0.AddSeconds(1), w);
        t.WriteStore("1#12", Records(("Kaleid Sumner", Guid1, T0)));
        var pass = new ConvertPass(t.Install, new ConvertOptions());

        Assert.True(pass.Run().Characters.Single().NearlySquare);
        var again = pass.Run();
        Assert.Empty(again.Written);
        Assert.True(again.Characters.Single().NearlySquare);
    }

    [Fact]
    public void A_screenshot_that_cannot_be_deleted_is_a_warning_not_the_end_of_the_pass()
    {
        using var t = new TempInstall();
        Capture(t, "1#12", "Kaleid Sumner", Guid1, T0);
        t.WriteStore("1#12", Records(("Kaleid Sumner", Guid1, T0)));
        var black = Path.Combine(t.Install.Screenshots, TestData.ShotName(T0));
        File.SetAttributes(black, FileAttributes.ReadOnly);       // File.Delete: UnauthorizedAccessException
        try
        {
            var report = new ConvertPass(t.Install, new ConvertOptions()).Run();
            Assert.Equal(["Kaleid Sumner"], report.Written);
            Assert.Contains(report.Warnings, x => x.Contains("could not delete"));
            Assert.True(File.Exists(black));
            // The other file went, and the manifest was still written.
            Assert.False(File.Exists(Path.Combine(t.Install.Screenshots, TestData.ShotName(T0.AddSeconds(1)))));
            Assert.Contains(Guid1, File.ReadAllText(new CutoutFolder(t.Install.CutoutAddonDir).ManifestPath));
        }
        finally
        {
            File.SetAttributes(black, FileAttributes.Normal);
        }
    }

    [Fact]
    public void One_capture_that_fails_does_not_take_the_others_with_it()
    {
        using var t = new TempInstall();
        Capture(t, "1#12", "Aaa First", "Player-1-AAAAAAAA", T0);
        Capture(t, "1#12", "Bbb Second", "Player-1-BBBBBBBB", T0.AddMinutes(1));
        t.WriteStore("1#12", Records(("Aaa First", "Player-1-AAAAAAAA", T0), ("Bbb Second", "Player-1-BBBBBBBB", T0.AddMinutes(1))));
        // Where the first cutout has to go there is a FOLDER of that name: the write fails.
        var f = new CutoutFolder(t.Install.CutoutAddonDir);
        Directory.CreateDirectory(Path.Combine(f.CutoutsDir, "aaa-first.tga"));

        var report = new ConvertPass(t.Install, new ConvertOptions()).Run();

        Assert.Equal(["Bbb Second"], report.Written);
        Assert.Equal([CharacterState.Failed, CharacterState.Portrait], report.Characters.Select(c => c.State));
        Assert.Contains(report.Warnings, x => x.StartsWith("Aaa First"));
        Assert.Contains("Player-1-BBBBBBBB", File.ReadAllText(f.ManifestPath));
        // What failed keeps its screenshots, to be tried again.
        Assert.True(File.Exists(Path.Combine(t.Install.Screenshots, TestData.ShotName(T0))));
    }

    [Fact]
    public void A_screenshot_this_codec_will_never_read_is_not_waited_for()
    {
        using var t = new TempInstall();
        Capture(t, "1#12", "Kaleid Sumner", Guid1, T0);
        t.WriteStore("1#12", Records(("Kaleid Sumner", Guid1, T0)));
        var black = Path.Combine(t.Install.Screenshots, TestData.ShotName(T0));
        var bytes = File.ReadAllBytes(black);
        bytes[16] = 16;                                           // 16 bits a pixel: not a depth it reads
        File.WriteAllBytes(black, bytes);
        var pass = new ConvertPass(t.Install, new ConvertOptions());

        var clock = System.Diagnostics.Stopwatch.StartNew();
        var status = pass.Run().Characters.Single();
        Assert.Equal(CharacterState.Rejected, status.State);
        Assert.Contains("cannot be read", status.Note);
        // Four waits of half a second were spent on it before.
        Assert.True(clock.ElapsedMilliseconds < 1500, $"waited {clock.ElapsedMilliseconds} ms for a file that will not change");
        Assert.True(File.Exists(black));
    }

    [Fact]
    public void A_screenshot_cut_short_is_waited_for_once_then_left_until_it_changes()
    {
        using var t = new TempInstall();
        Capture(t, "1#12", "Kaleid Sumner", Guid1, T0);
        t.WriteStore("1#12", Records(("Kaleid Sumner", Guid1, T0)));
        var black = Path.Combine(t.Install.Screenshots, TestData.ShotName(T0));
        var whole = File.ReadAllBytes(black);
        File.WriteAllBytes(black, whole[..(whole.Length / 2)]);
        var pass = new ConvertPass(t.Install, new ConvertOptions());

        Assert.Equal(CharacterState.Missing, pass.Run().Characters.Single().State);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        Assert.Equal(CharacterState.Missing, pass.Run().Characters.Single().State);
        Assert.True(clock.ElapsedMilliseconds < 1500, $"read it again: {clock.ElapsedMilliseconds} ms");

        // The client finishes writing it: the file changed, so it is read again.
        File.WriteAllBytes(black, whole);
        Assert.Equal(["Kaleid Sumner"], pass.Run().Written);
    }

    [Fact]
    public void Cancelling_stops_between_captures_and_still_reports()
    {
        using var t = new TempInstall();
        Capture(t, "1#12", "Kaleid Sumner", Guid1, T0);
        t.WriteStore("1#12", Records(("Kaleid Sumner", Guid1, T0)));
        using var stop = new CancellationTokenSource();
        stop.Cancel();

        var report = new ConvertPass(t.Install, new ConvertOptions()).Run(stop.Token);
        Assert.Empty(report.Written);
        Assert.True(File.Exists(Path.Combine(t.Install.Screenshots, TestData.ShotName(T0))));
    }

    [Fact]
    public void A_warning_is_in_every_report_and_in_the_log_once()
    {
        using var t = new TempInstall();
        t.WriteStore("1#12", TestData.SavedVariables([TestData.Record("A", Guid1, 1, T0)], version: 2));
        var log = new List<string>();
        var pass = new ConvertPass(t.Install, new ConvertOptions(), log.Add);

        Assert.Single(pass.Run().Warnings);
        Assert.Single(pass.Run().Warnings);
        Assert.Single(log);
    }

    [Fact]
    public void The_report_lists_every_portrait_and_every_capture_once()
    {
        using var t = new TempInstall();
        var f = new CutoutFolder(t.Install.CutoutAddonDir);
        f.EnsureToc();
        // Two portraits from before captures were recorded, one of them of a character who
        // has captured since (its screenshots are gone); a capture that converts; one that does not.
        TgaCodec.Write(Path.Combine(f.CutoutsDir, "karuzo-elegia.tga"), TestData.Solid(4, 4, 1, 1, 1));
        TgaCodec.Write(Path.Combine(f.CutoutsDir, "old-friend.tga"), TestData.Solid(4, 4, 1, 1, 1));
        Capture(t, "1#1", "Kaleid Sumner", Guid1, T0);
        var same = TestData.Solid(400, 1200, 90, 90, 90);
        t.WriteShot(T0.AddMinutes(5), same);
        t.WriteShot(T0.AddMinutes(5).AddSeconds(1), same);
        t.WriteStore("1#1", Records(
            ("Kaleid Sumner", Guid1, T0), ("Old Friend", "Player-1-BBBBBBBB", T0.AddMinutes(-30)),
            ("Twice Shot", "Player-1-CCCCCCCC", T0.AddMinutes(5))));

        var report = new ConvertPass(t.Install, new ConvertOptions()).Run();

        Assert.True(report.ManifestWritten);
        var rows = report.Portraits!;
        Assert.Equal(["Kaleid Sumner", "Karuzo Elegia", "Old Friend", "Twice Shot"], rows.Select(r => r.Name));
        Assert.Equal([PortraitSource.ByGuid, PortraitSource.File, PortraitSource.ByName, PortraitSource.None], rows.Select(r => r.Source));
        Assert.Equal([CaptureOutcome.Converted, CaptureOutcome.None, CaptureOutcome.NoScreenshots, CaptureOutcome.Unusable], rows.Select(r => r.Outcome));
        Assert.Equal([false, false, false, true], rows.Select(r => r.NeedsAttention));
        Assert.All(rows.Where(r => r.Ready), r => Assert.NotNull(r.FileModified));
        Assert.Equal("3", PassText.Count(rows));
        Assert.Equal("1 character needs attention", PassText.Attention(rows));
    }

    [Fact]
    public void A_manifest_that_could_not_be_written_is_said_in_the_report()
    {
        using var t = new TempInstall();
        Capture(t, "1#1", "Kaleid Sumner", Guid1, T0);
        t.WriteStore("1#1", Records(("Kaleid Sumner", Guid1, T0)));
        var f = new CutoutFolder(t.Install.CutoutAddonDir);
        f.EnsureToc();
        // Where the manifest has to go there is a FOLDER of that name.
        Directory.CreateDirectory(f.ManifestPath);

        var report = new ConvertPass(t.Install, new ConvertOptions()).Run();

        Assert.Equal(["Kaleid Sumner"], report.Written);
        Assert.False(report.ManifestWritten);
        Assert.Contains(report.Warnings, w => w.Contains("could not write the manifest"));
        // What is on disk is still listed: the files are there, the game has not been told.
        Assert.Equal("kaleid-sumner.tga", report.Portraits!.Single().FileName);
    }

    [Fact]
    public void With_nothing_to_convert_no_addon_folder_is_created()
    {
        using var t = new TempInstall();
        t.WriteStore("1#1", "AltStablePortraits = nil\n");
        var report = new ConvertPass(t.Install, new ConvertOptions()).Run();
        Assert.False(report.FolderCreated);
        Assert.False(Directory.Exists(t.Install.CutoutAddonDir));
    }
}

public class InstallAndSettingsTests
{
    [Fact]
    public void A_flavour_folder_is_one_with_a_wow_executable()
    {
        using var t = new TempInstall();
        Assert.True(WowInstallLocator.IsFlavorDir(t.Install.FlavorDir));
        Directory.CreateDirectory(Path.Combine(t.Root, "_empty_"));
        Directory.CreateDirectory(Path.Combine(t.Root, "Data"));
        Assert.Equal([t.Install.FlavorDir], WowInstallLocator.FlavorsUnder(t.Root));
        Assert.Equal("_classic_beta_", t.Install.Flavor);
        Assert.EndsWith(Path.Combine("Interface", "AddOns", "AltStableCutouts"), t.Install.CutoutAddonDir);
    }

    [Fact]
    public void Settings_round_trip_and_survive_a_broken_file()
    {
        using var t = new TempInstall();
        var dir = Path.Combine(t.Root, "appdata");
        new Settings { KeepScreenshots = true, WowFlavorDir = "X" }.Save(dir);
        Assert.Equal(new Settings { KeepScreenshots = true, WowFlavorDir = "X" }, Settings.Load(dir));
        File.WriteAllText(Path.Combine(dir, "settings.json"), "{ not json");
        Assert.Equal(new Settings(), Settings.Load(dir));
    }

    [Fact]
    public void A_log_that_cannot_be_written_does_not_take_the_app_with_it()
    {
        using var t = new TempInstall();
        var dir = Path.Combine(t.Root, "appdata");
        // A folder where the file should be: appending to it is refused, not an IOException.
        Directory.CreateDirectory(Path.Combine(dir, "log.txt"));
        new Log(dir).Write("a line");
    }

    [Fact]
    public void The_watcher_runs_a_pass_after_a_screenshot_appears()
    {
        using var t = new TempInstall();
        using var ran = new ManualResetEventSlim();
        using var w = new Watcher([(t.Install.Screenshots, "*.tga", false)], ran.Set,
            debounce: TimeSpan.FromMilliseconds(100), poll: TimeSpan.FromHours(1));
        File.WriteAllBytes(Path.Combine(t.Install.Screenshots, "WoWScrnShot_092926_010638.tga"), [1]);
        Assert.True(ran.Wait(TimeSpan.FromSeconds(10)), "no pass ran");
    }

    [Fact]
    public void A_pass_that_throws_is_reported_and_the_next_one_still_runs()
    {
        using var t = new TempInstall();
        using var failed = new ManualResetEventSlim();
        using var ran = new ManualResetEventSlim();
        var calls = 0;
        using var w = new Watcher([(t.Install.Screenshots, "*.tga", false)],
            () =>
            {
                if (Interlocked.Increment(ref calls) == 1) throw new UnauthorizedAccessException("in use");
                ran.Set();
            },
            debounce: TimeSpan.FromMilliseconds(50), poll: TimeSpan.FromHours(1),
            failed: ex => { if (ex is UnauthorizedAccessException) failed.Set(); });

        w.Trigger();
        Assert.True(failed.Wait(TimeSpan.FromSeconds(10)), "the failure was not reported");
        w.Trigger();
        Assert.True(ran.Wait(TimeSpan.FromSeconds(10)), "no pass ran after the one that threw");
    }

    [Fact]
    public void Pausing_stops_a_pass_that_was_already_counting_down()
    {
        using var t = new TempInstall();
        using var ran = new ManualResetEventSlim();
        using var w = new Watcher([(t.Install.Screenshots, "*.tga", false)], ran.Set,
            debounce: TimeSpan.FromMilliseconds(200), poll: TimeSpan.FromHours(1));
        w.Trigger();
        w.Paused = true;
        Assert.False(ran.Wait(TimeSpan.FromMilliseconds(900)));
        // And it is a pause, not a stop.
        w.Paused = false;
        w.Trigger();
        Assert.True(ran.Wait(TimeSpan.FromSeconds(10)), "no pass ran after the pause ended");
    }

    [Fact]
    public void Pausing_drops_the_rerun_queued_behind_a_running_pass()
    {
        using var t = new TempInstall();
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var calls = 0;
        using var w = new Watcher([(t.Install.Screenshots, "*.tga", false)],
            () =>
            {
                Interlocked.Increment(ref calls);
                started.Set();
                release.Wait(TimeSpan.FromSeconds(10));
            },
            debounce: TimeSpan.FromMilliseconds(20), poll: TimeSpan.FromHours(1));

        w.Trigger();
        Assert.True(started.Wait(TimeSpan.FromSeconds(10)));
        w.Trigger();                                              // queues a rerun behind the pass
        Thread.Sleep(300);
        w.Paused = true;
        release.Set();
        Thread.Sleep(500);
        Assert.Equal(1, Volatile.Read(ref calls));
    }

    [Fact]
    public void A_paused_watcher_runs_nothing()
    {
        using var t = new TempInstall();
        using var ran = new ManualResetEventSlim();
        using var w = new Watcher([(t.Install.Screenshots, "*.tga", false)], ran.Set,
            debounce: TimeSpan.FromMilliseconds(50), poll: TimeSpan.FromHours(1)) { Paused = true };
        File.WriteAllBytes(Path.Combine(t.Install.Screenshots, "WoWScrnShot_092926_010638.tga"), [1]);
        Assert.False(ran.Wait(TimeSpan.FromMilliseconds(800)));
    }
}
