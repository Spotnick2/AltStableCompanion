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
        Assert.Equal(t.Install.FlavorDir, WowInstallLocator.Detect(t.Install.FlavorDir)!.FlavorDir);
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
