using AltStableCompanion.Core;

namespace AltStableCompanion.Core.Tests;

/// <summary>
/// The controller's rules, run for real: temporary installs, its own watcher, its own passes.
/// Nothing here waits a fixed time for something to HAPPEN - it waits until it has, with a
/// limit. Where a test has to show that something did NOT happen, it waits for a later event
/// that could only come after.
/// </summary>
public class ControllerTests
{
    private static readonly DateTime T0 = new(2026, 9, 29, 1, 6, 38);
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(20);

    private static string Data(TempInstall t) => Path.Combine(t.Root, "appdata");

    private static Controller Started(TempInstall t, string? wowDir = null, Func<WowInstall?>? detect = null)
    {
        var c = new Controller(new StartupOptions(WowDir: wowDir, DataDir: Data(t)),
            detect ?? (() => throw new InvalidOperationException("detection was used")));
        c.Start();
        return c;
    }

    private static void Until(Func<bool> happened, string what)
    {
        var limit = DateTime.UtcNow + Limit;
        while (!happened())
        {
            Assert.True(DateTime.UtcNow < limit, "never happened: " + what);
            Thread.Sleep(25);
        }
    }

    private static void FirstPass(Controller c) =>
        Until(() => c.Current.Shell is { Report: not null, Converting: false }, "the first pass");

    // One more pass, asked for, and over.
    private static PassReport Pass(Controller c)
    {
        PassReport? got = null;
        void Done(PassReport r) => got = r;
        c.PassCompleted += Done;
        try
        {
            c.ConvertNow();
            Until(() => got is not null, "the pass asked for");
            return got!;
        }
        finally
        {
            c.PassCompleted -= Done;
        }
    }

    private static void Capture(TempInstall t, string account, string name, string guid, DateTime at)
    {
        var (b, w) = TestData.Pair(400, 1200, 150, 250, 100, 700);
        t.WriteShot(at, b);
        t.WriteShot(at.AddSeconds(1), w);
        t.WriteStore(account, TestData.SavedVariables(
            [TestData.Record(name, guid, 1, at), TestData.Record(name, guid, 2, at.AddSeconds(1))]));
    }

    private static void Malformed(TempInstall t, string chosen)
    {
        Directory.CreateDirectory(Data(t));
        File.WriteAllText(Path.Combine(Data(t), "settings.json"),
            "{ \"WowFlavorDir\": " + System.Text.Json.JsonSerializer.Serialize(chosen) + ", \"KeepScreenshots\": false, }");
    }

    [Fact]
    public void A_saved_folder_is_watched_and_its_capture_converted()
    {
        using var t = new TempInstall();
        new Settings { WowFlavorDir = t.Install.FlavorDir }.Save(Data(t));
        Capture(t, "1#1", "Aaa", "Player-1-AAAAAAAA", T0);

        using var c = Started(t);
        FirstPass(c);

        var now = c.Current;
        Assert.Equal(t.Install.FlavorDir, now.Shell.Install!.FlavorDir);
        Assert.False(now.Pinned);
        Assert.Equal(CharacterState.Portrait, now.Shell.Report!.Characters.Single().State);
        Assert.True(File.Exists(Path.Combine(t.Install.CutoutAddonDir, "Cutouts", "aaa.tga")));
    }

    [Fact]
    public void A_folder_that_is_not_known_stays_unknown_until_the_player_chooses()
    {
        // The settings cannot be read. The folder the player chose is in them; the one that
        // detection would find is another game.
        using var t = new TempInstall();
        using var other = new TempInstall();
        Capture(other, "1#1", "Victim", "Player-1-AAAAAAAA", T0);
        Malformed(t, @"D:\Games\WoW\_classic_beta_");
        WowInstall? Detect() => other.Install;

        using (var c = Started(t, detect: Detect))
        {
            Assert.Null(c.Current.Shell.Install);
            Assert.True(c.Current.KeepScreenshots);
            Assert.Contains("chosen again", c.Current.SettingsProblem);

            // Something else is changed. That is not choosing a folder.
            c.SetKeepScreenshots(false);
            c.SetPaused(true);
            c.SetPaused(false);
            Assert.Null(c.Current.Shell.Install);
            Assert.Contains("chosen again", c.Current.SettingsProblem);
        }

        // What could not be read is kept, and what was written says the folder is not known.
        Assert.Contains(@"D:\\Games", File.ReadAllText(Path.Combine(Data(t), "settings.unreadable.json")));
        var saved = Settings.Load(Data(t), out var problem);
        Assert.Null(problem);
        Assert.True(saved.InstallUnknown);
        Assert.False(saved.KeepScreenshots);

        // The next start: the settings read, and still nothing is detected.
        using (var c = Started(t, detect: Detect))
        {
            Assert.Null(c.Current.Shell.Install);
            Assert.Contains("not known", c.Current.Shell.InstallProblem);
            Assert.Contains("chosen again", c.Current.SettingsProblem);
        }
        Assert.Equal(2, Directory.EnumerateFiles(other.Install.Screenshots).Count());
        Assert.False(Directory.Exists(other.Install.CutoutAddonDir));

        // The player chooses. From then on it is known, and stays known.
        using (var c = Started(t, detect: Detect))
        {
            Assert.True(c.Browse(t.Install.FlavorDir));
            Assert.Null(c.Current.SettingsProblem);
            FirstPass(c);
        }
        Assert.False(Settings.Load(Data(t)).InstallUnknown);
        using (var c = Started(t, detect: Detect))
        {
            Assert.Equal(t.Install.FlavorDir, c.Current.Shell.Install!.FlavorDir);
            FirstPass(c);
        }
    }

    [Fact]
    public void Detect_again_is_the_player_choosing_too()
    {
        using var t = new TempInstall();
        Malformed(t, @"D:\Games\WoW\_classic_beta_");
        using (var c = Started(t, detect: () => t.Install))
        {
            Assert.Null(c.Current.Shell.Install);
            c.DetectAgain();
            Assert.Equal(t.Install.FlavorDir, c.Current.Shell.Install!.FlavorDir);
            Assert.Null(c.Current.SettingsProblem);
            FirstPass(c);
        }
        Assert.False(Settings.Load(Data(t)).InstallUnknown);
    }

    [Fact]
    public void A_folder_named_on_the_command_line_is_used_and_the_unknown_one_stays_unknown()
    {
        using var t = new TempInstall();
        Malformed(t, @"D:\Games\WoW\_classic_beta_");
        var before = File.ReadAllText(Path.Combine(Data(t), "settings.json"));
        using (var c = Started(t, wowDir: t.Install.FlavorDir))
        {
            FirstPass(c);
            Assert.True(c.Current.Pinned);
            Assert.True(c.Current.KeepScreenshots);
            Assert.False(c.Browse(t.Install.FlavorDir));         // pinned: not the player's to change
        }
        // The pass wrote down the notice it may come to owe. What could not be read is kept
        // beside it, and what was written still says that the folder is not known: a later
        // start WITHOUT the folder on the command line detects nothing.
        Assert.Equal(before, File.ReadAllText(Path.Combine(Data(t), "settings.unreadable.json")));
        var saved = Settings.Load(Data(t), out var problem);
        Assert.Null(problem);
        Assert.True(saved.InstallUnknown);
        Assert.True(saved.KeepScreenshots);
        Assert.Null(saved.WowFlavorDir);
        using var next = Started(t, detect: () => t.Install);
        Assert.Null(next.Current.Shell.Install);
    }

    [Fact]
    public void A_restart_notice_that_could_not_be_written_is_written_later()
    {
        using var t = new TempInstall();
        new Settings { WowFlavorDir = t.Install.FlavorDir }.Save(Data(t));
        var file = Path.Combine(Data(t), "settings.json");
        File.SetAttributes(file, FileAttributes.ReadOnly);
        try
        {
            Capture(t, "1#1", "Aaa", "Player-1-AAAAAAAA", T0);
            using (var c = Started(t))
            {
                FirstPass(c);
                // The addon is there, the notice is shown - and it is nowhere on disk.
                Assert.True(File.Exists(new CutoutFolder(t.Install.CutoutAddonDir).TocPath));
                Assert.True(c.Current.RestartNotice);
                Assert.Contains("could not be saved", c.Current.SettingsProblem);
                Assert.False(Settings.Load(Data(t)).OwesRestartNotice(t.Install.FlavorDir));

                // The file can be written again. Nobody changes a setting; a pass comes round.
                File.SetAttributes(file, FileAttributes.Normal);
                Pass(c);
                Assert.Null(c.Current.SettingsProblem);
            }
            Assert.True(Settings.Load(Data(t)).OwesRestartNotice(t.Install.FlavorDir));

            // And after a restart the player is still told.
            using (var c = Started(t))
            {
                Assert.True(c.Current.RestartNotice);
                c.DismissRestartNotice();
                Assert.False(c.Current.RestartNotice);
            }
            using (var c = Started(t))
            {
                Assert.False(c.Current.RestartNotice);
            }
        }
        finally
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }
    }

    [Fact]
    public void An_addon_that_was_already_there_is_owed_no_notice()
    {
        using var t = new TempInstall();
        new CutoutFolder(t.Install.CutoutAddonDir).EnsureToc();
        new Settings { WowFlavorDir = t.Install.FlavorDir }.Save(Data(t));
        Capture(t, "1#1", "Aaa", "Player-1-AAAAAAAA", T0);
        using var c = Started(t);
        FirstPass(c);
        Assert.Single(c.Current.Shell.Report!.Written);
        Assert.False(c.Current.RestartNotice);
        Assert.Empty(Settings.Load(Data(t)).RestartNoticeInstalls);
    }

    [Fact]
    public void A_pass_that_waited_does_not_start_once_the_player_has_paused()
    {
        // A's pass is slow: one of its screenshots ends early, and is waited for. While it
        // runs the player Browses to B - whose first look now waits its turn - and pauses.
        using var a = new TempInstall();
        using var b = new TempInstall();
        Capture(a, "1#1", "Slow", "Player-1-AAAAAAAA", T0);
        var cut = Path.Combine(a.Install.Screenshots, TestData.ShotName(T0));
        File.WriteAllBytes(cut, File.ReadAllBytes(cut)[..200]);
        Capture(b, "1#1", "Bbb", "Player-2-BBBBBBBB", T0);
        new Settings { WowFlavorDir = a.Install.FlavorDir }.Save(Data(a));

        using var c = Started(a);
        Until(() => c.Current.Shell.Converting, "A's pass starting");
        Assert.True(c.Browse(b.Install.FlavorDir));
        c.SetPaused(true);
        Until(() => !c.Current.Shell.Converting, "A's pass ending");

        // A's report is A's: the window is on B now.
        Assert.Equal(b.Install.FlavorDir, c.Current.Shell.Install!.FlavorDir);
        Assert.Null(c.Current.Shell.Report);

        // Anything that was going to run in B has had its turn by the time a pass that WAS
        // asked for is over - and that one converts what nothing had touched.
        var report = Pass(c);
        Assert.Equal(["Bbb"], report.Written);
        Assert.True(c.Current.Shell.Paused);
    }

    [Fact]
    public void Pausing_is_remembered_and_stops_the_first_look()
    {
        using var t = new TempInstall();
        new Settings { WowFlavorDir = t.Install.FlavorDir, Paused = true }.Save(Data(t));
        Capture(t, "1#1", "Aaa", "Player-1-AAAAAAAA", T0);
        using var c = Started(t);
        Assert.True(c.Current.Shell.Paused);

        // The pass asked for is the first to run: it finds the capture untouched.
        Assert.Equal(["Aaa"], Pass(c).Written);

        c.SetPaused(false);
        Assert.False(Settings.Load(Data(t)).Paused);
    }

    [Fact]
    public void Keep_screenshots_is_for_the_next_pass_and_is_remembered()
    {
        using var t = new TempInstall();
        new Settings { WowFlavorDir = t.Install.FlavorDir }.Save(Data(t));
        using var c = Started(t);
        FirstPass(c);

        c.SetKeepScreenshots(true);
        Capture(t, "1#1", "Aaa", "Player-1-AAAAAAAA", T0);
        var report = Pass(c);
        if (report.Written.Count == 0) report = Pass(c);         // the watcher may have got there first
        Assert.True(File.Exists(Path.Combine(t.Install.Screenshots, TestData.ShotName(T0))));
        Assert.True(Settings.Load(Data(t)).KeepScreenshots);
    }

    [Fact]
    public void A_folder_that_is_not_wow_changes_nothing()
    {
        using var t = new TempInstall();
        new Settings { WowFlavorDir = t.Install.FlavorDir }.Save(Data(t));
        using var c = Started(t);
        Assert.False(c.Browse(t.Install.Screenshots));
        Assert.Equal(t.Install.FlavorDir, c.Current.Shell.Install!.FlavorDir);
        Assert.Equal(t.Install.FlavorDir, Settings.Load(Data(t)).WowFlavorDir);
    }

    [Fact]
    public async Task Stopping_waits_for_the_pass_in_hand_and_starts_no_other()
    {
        using var t = new TempInstall();
        Capture(t, "1#1", "Slow", "Player-1-AAAAAAAA", T0);
        var cut = Path.Combine(t.Install.Screenshots, TestData.ShotName(T0));
        File.WriteAllBytes(cut, File.ReadAllBytes(cut)[..200]);
        new Settings { WowFlavorDir = t.Install.FlavorDir }.Save(Data(t));

        using var c = Started(t);
        Until(() => c.Current.Shell.Converting, "the pass starting");
        var stopped = c.StopAsync();
        Assert.True(c.Current.Shell.Stopping);
        await stopped.WaitAsync(Limit);
        Assert.False(c.Current.Shell.Converting);

        c.ConvertNow();
        Assert.False(c.Browse(t.Install.FlavorDir));
        Assert.Contains("stopped", File.ReadAllText(c.LogPath));
    }
}
