using AltStableCompanion.Core;

namespace AltStableCompanion.Core.Tests;

public class StartupOptionsTests
{
    [Fact]
    public void No_arguments_is_an_ordinary_start()
    {
        var (options, error) = StartupOptions.Parse([]);
        Assert.Null(error);
        Assert.Equal(new StartupOptions(), options);
        Assert.False(options!.Explicit);
    }

    [Fact]
    public void The_folders_named_are_the_folders_used()
    {
        using var t = new TempInstall();
        var data = Path.Combine(t.Root, "data");
        var (options, error) = StartupOptions.Parse(["--minimized", "--wow-dir", t.Install.FlavorDir + "\\", "--data-dir", data]);
        Assert.Null(error);
        Assert.True(options!.Minimized);
        Assert.Equal(t.Install.FlavorDir, options.WowDir);
        Assert.Equal(data, options.DataDir);
        Assert.True(options.Explicit);
    }

    [Fact]
    public void Reading_the_command_line_changes_nothing_on_disk()
    {
        // The data folder named BEFORE an option that is wrong: "nothing was started" has to
        // mean that nothing was created either.
        using var t = new TempInstall();
        var data = Path.Combine(t.Root, "data");
        Assert.Null(StartupOptions.Parse(["--data-dir", data, "--wow-dir", Path.Combine(t.Root, "_typo_")]).Options);
        Assert.False(Directory.Exists(data));

        var (options, _) = StartupOptions.Parse(["--data-dir", data]);
        Assert.False(Directory.Exists(data));
        Assert.Null(options!.PrepareDataDir());
        Assert.True(Directory.Exists(data));
        Assert.Empty(Directory.EnumerateFileSystemEntries(data));
        Assert.Null(new StartupOptions().PrepareDataDir());
    }

    [Fact]
    public void A_wow_dir_that_is_not_a_flavour_folder_stops_the_app()
    {
        // The mistake this exists for: a mistyped folder must not become "no folder given",
        // because no folder given means detection - the real install.
        using var t = new TempInstall();
        var (options, error) = StartupOptions.Parse(["--wow-dir", Path.Combine(t.Root, "_classic_bta_")]);
        Assert.Null(options);
        Assert.Contains("--wow-dir", error);

        // The WoW folder itself is not a flavour folder either: say so, do not guess which.
        (options, error) = StartupOptions.Parse(["--wow-dir", t.Root]);
        Assert.Null(options);
        Assert.NotNull(error);
    }

    [Theory]
    [InlineData("--wow-dir")]                              // no folder after it
    [InlineData("--wow-dir", "--minimized")]               // an option where the folder should be
    [InlineData("--data-dir", "")]
    [InlineData("--data-dir", "--minimized")]              // and is not taken for a folder name
    [InlineData("--data-dir", "C:\\a", "--data-dir", "C:\\b")]
    [InlineData("--wowdir", "C:\\x")]                      // a misspelt option is not ignored
    [InlineData("minimized")]
    [InlineData("--wow-dir", "C:\\a\0b")]
    public void A_command_line_that_is_wrong_is_an_error_not_a_default(params string[] args)
    {
        var (options, error) = StartupOptions.Parse(args);
        Assert.Null(options);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void A_data_dir_that_cannot_be_written_stops_the_app()
    {
        using var t = new TempInstall();
        var file = Path.Combine(t.Root, "a-file");
        File.WriteAllText(file, "");
        var (options, _) = StartupOptions.Parse(["--data-dir", Path.Combine(file, "under-a-file")]);
        Assert.Contains("--data-dir", options!.PrepareDataDir());
    }
}

public class ResolvedInstallTests
{
    private static readonly WowInstall Detected = new(@"C:\detected\_classic_beta_");

    [Fact]
    public void A_pinned_folder_is_used_and_nothing_is_detected()
    {
        using var t = new TempInstall();
        var r = ResolvedInstall.Resolve(t.Install.FlavorDir, saved: @"C:\saved\_x_", detect: () => throw new InvalidOperationException("detected"));
        Assert.Equal(t.Install.FlavorDir, r.Install!.FlavorDir);
        Assert.True(r.Pinned);
        Assert.Null(r.Problem);
    }

    [Fact]
    public void A_pinned_folder_that_is_gone_is_a_problem_never_a_fallback()
    {
        using var t = new TempInstall();
        var r = ResolvedInstall.Resolve(Path.Combine(t.Root, "_gone_"), saved: t.Install.FlavorDir, detect: () => Detected);
        Assert.Null(r.Install);
        Assert.True(r.Pinned);
        Assert.NotNull(r.Problem);
    }

    [Fact]
    public void A_saved_folder_that_is_gone_is_a_problem_never_a_fallback()
    {
        using var t = new TempInstall();
        var r = ResolvedInstall.Resolve(null, saved: Path.Combine(t.Root, "_gone_"), detect: () => Detected);
        Assert.Null(r.Install);
        Assert.Contains("_gone_", r.Problem);
    }

    [Fact]
    public void Settings_that_could_not_be_read_are_not_a_reason_to_detect()
    {
        // A settings file with a stray comma: the folder the player chose is in it, unread.
        // Detection would pick the game in Program Files and the first pass would delete there.
        using var t = new TempInstall();
        var r = ResolvedInstall.Resolve(null, saved: null, detect: () => Detected, settingsProblem: "line 3: ','");
        Assert.Null(r.Install);
        Assert.Contains("settings", r.Problem);

        // What was named on the command line is still what is used.
        Assert.Equal(t.Install.FlavorDir,
            ResolvedInstall.Resolve(t.Install.FlavorDir, null, () => Detected, "line 3: ','").Install!.FlavorDir);
    }

    [Fact]
    public void Detection_is_for_when_no_folder_was_ever_given()
    {
        using var t = new TempInstall();
        Assert.Equal(t.Install.FlavorDir, ResolvedInstall.Resolve(null, t.Install.FlavorDir, () => Detected).Install!.FlavorDir);
        Assert.Equal(Detected, ResolvedInstall.Resolve(null, null, () => Detected).Install);
        Assert.Equal(Detected, ResolvedInstall.Resolve(null, "  ", () => Detected).Install);
        Assert.NotNull(ResolvedInstall.Resolve(null, null, () => null).Problem);
    }

    [Fact]
    public void Browse_takes_a_flavour_folder_or_the_wow_folder_holding_forever()
    {
        using var t = new TempInstall();
        Assert.Equal(t.Install.FlavorDir, ResolvedInstall.FromPicked(t.Install.FlavorDir + "\\")!.FlavorDir);
        Assert.Equal(t.Install.FlavorDir, ResolvedInstall.FromPicked(t.Root)!.FlavorDir);
        Assert.Null(ResolvedInstall.FromPicked(t.Install.Screenshots));
    }
}

public class PassTextTests
{
    private static readonly DateTime T0 = new(2026, 9, 29, 1, 6, 38);

    private static PassReport Report(int written = 0, bool folderCreated = false, bool stale = false,
        int accounts = 1, int refused = 0, params CharacterState[] states) => new(
        [.. states.Select((s, i) => new CharacterStatus($"g{i}", $"Name {i}", T0, s, null))],
        [.. Enumerable.Range(1, written).Select(i => $"Name {i}")],
        0, stale ? (T0, null) : null, folderCreated, [], accounts, refused);

    private static ShellState With(TempInstall t, PassReport? report = null, bool addon = true) =>
        new(Install: Installed(t, addon), Report: report);

    private static WowInstall Installed(TempInstall t, bool addon)
    {
        if (addon)
        {
            Directory.CreateDirectory(Path.Combine(t.Install.AddOnsDir, "AltStable"));
            File.WriteAllText(Path.Combine(t.Install.AddOnsDir, "AltStable", "AltStable.toc"), "");
        }
        return t.Install;
    }

    [Fact]
    public void The_status_line_says_the_most_useful_thing_first()
    {
        using var t = new TempInstall();
        var quiet = With(t, Report(states: CharacterState.Portrait));
        Assert.Equal("Watching", PassText.StatusLine(quiet));
        Assert.StartsWith("Watching - in game", PassText.StatusLine(With(t, Report())));
        Assert.Equal("Starting...", PassText.StatusLine(With(t)));

        // Each of these outranks everything after it.
        var all = quiet with
        {
            Report = Report(stale: true, refused: 1, accounts: 0),
            Paused = true, Converting = true, Stopping = true, LastError = "boom",
        };
        Assert.StartsWith("Finishing", PassText.StatusLine(all));
        all = all with { Stopping = false };
        Assert.Equal("Converting - watching is paused", PassText.StatusLine(all));
        Assert.Equal("Converting...", PassText.StatusLine(all with { Paused = false }));
        all = all with { Converting = false };
        Assert.StartsWith("The last pass failed", PassText.StatusLine(all));
        all = all with { LastError = null };
        Assert.Contains("update the companion", PassText.StatusLine(all));
        all = all with { Report = Report(stale: true, accounts: 0) };
        Assert.Equal("Paused", PassText.StatusLine(all));
        Assert.StartsWith("Paused - nothing", PassText.StatusLine(all with { Report = null }));
        all = all with { Paused = false };
        Assert.StartsWith("No AltStable data yet", PassText.StatusLine(all));
        all = all with { Report = Report(stale: true) };
        Assert.StartsWith("Newer screenshots found", PassText.StatusLine(all));
    }

    [Fact]
    public void Without_an_install_the_line_says_why_and_what_to_do()
    {
        Assert.Equal("Couldn't find the WoW folder - Browse...", PassText.StatusLine(new ShellState()));
        Assert.Equal("It is gone: X - Browse...", PassText.StatusLine(new ShellState(InstallProblem: "It is gone: X", Converting: true)));
    }

    [Fact]
    public void An_install_without_the_addon_says_so()
    {
        using var t = new TempInstall();
        Assert.Equal("AltStable is not installed in this flavour",
            PassText.StatusLine(With(t, Report(states: CharacterState.Portrait), addon: false)));
    }

    [Fact]
    public void The_reload_hint_is_a_hint()
    {
        // Any screenshot newer than the records raises it, a hand-taken one included: it must
        // not claim that a capture is waiting.
        using var t = new TempInstall();
        var line = PassText.StatusLine(With(t, Report(stale: true)));
        Assert.Contains("If you just captured", line);
    }

    [Fact]
    public void The_tray_says_in_a_word_what_the_app_is_doing()
    {
        using var t = new TempInstall();
        var s = new ShellState(Install: t.Install, Paused: true, Converting: true, Stopping: true);
        Assert.Equal("AltStable Companion - finishing", PassText.TrayTip(s));
        Assert.Equal("AltStable Companion - converting", PassText.TrayTip(s with { Stopping = false }));
        Assert.Equal("AltStable Companion - paused", PassText.TrayTip(s with { Stopping = false, Converting = false }));
        Assert.Equal("AltStable Companion - watching", PassText.TrayTip(new ShellState(Install: t.Install)));
        Assert.Equal("AltStable Companion - no WoW folder", PassText.TrayTip(new ShellState(Converting: true)));
    }

    [Fact]
    public void A_balloon_is_for_portraits_written_and_nothing_else()
    {
        Assert.Null(PassText.Balloon(Report(states: [CharacterState.Rejected, CharacterState.Failed, CharacterState.Ambiguous])));
        Assert.Null(PassText.Balloon(Report(folderCreated: true)));

        Assert.Equal(("Portrait written: Name 1", "Reload in game to see it."), PassText.Balloon(Report(written: 1)));
        Assert.Equal(("3 portraits written", "Reload in game to see them."), PassText.Balloon(Report(written: 3)));

        var first = PassText.Balloon(Report(written: 1, folderCreated: true))!.Value;
        Assert.Equal("Portrait written: Name 1", first.Title);
        Assert.Contains("quit the game completely", first.Text);
    }

    [Fact]
    public void Every_state_has_its_own_label()
    {
        var labels = Enum.GetValues<CharacterState>().Select(PassText.StateLabel).ToList();
        Assert.Equal(labels.Count, labels.Distinct().Count());
        Assert.DoesNotContain(labels, string.IsNullOrWhiteSpace);
    }

    [Fact]
    public void Only_forever_is_the_flavour_the_cutouts_are_written_for()
    {
        Assert.Null(PassText.FlavorWarning(new WowInstall(@"C:\WoW\_classic_beta_")));
        Assert.Contains("_retail_", PassText.FlavorWarning(new WowInstall(@"C:\WoW\_retail_")));
    }

    [Fact]
    public void The_install_line_counts_the_accounts_once_a_pass_has_looked()
    {
        using var t = new TempInstall();
        var install = Installed(t, addon: true);
        Assert.Equal("AltStable: installed", PassText.InstallDetail(install, null));
        Assert.Equal("AltStable: installed - 1 account", PassText.InstallDetail(install, Report(accounts: 1)));
        Assert.Equal("AltStable: installed - 2 accounts", PassText.InstallDetail(install, Report(accounts: 2)));
    }
}

public class ShellCoreTests
{
    private static readonly DateTime T0 = new(2026, 9, 29, 1, 6, 38);

    [Fact]
    public void A_pass_counts_the_accounts_and_the_stores_it_cannot_read()
    {
        using var t = new TempInstall();
        t.WriteStore("1#1", "AltStablePortraits = nil\n");
        t.WriteStore("1#12", TestData.SavedVariables([TestData.Record("A", "Player-1-AAAA", 1, T0)], version: 2));
        var report = new ConvertPass(t.Install, new ConvertOptions()).Run();
        Assert.Equal(2, report.Accounts);
        Assert.Equal(1, report.Refused);
    }

    [Fact]
    public void The_options_change_and_the_pass_stays_the_same()
    {
        // "Keep screenshots" ticked between two passes: the second keeps them, and the pass
        // still knows the pair it looked at before and would not use.
        using var t = new TempInstall();
        var same = TestData.Solid(400, 1200, 90, 90, 90);
        var rejected = t.WriteShot(T0, same);
        t.WriteShot(T0.AddSeconds(1), same);
        const string a = "Player-1-AAAAAAAA", b = "Player-1-BBBBBBBB";
        static string Two(string name, string guid, DateTime at) => TestData.SavedVariables(
            [TestData.Record(name, guid, 1, at), TestData.Record(name, guid, 2, at.AddSeconds(1))]);
        t.WriteStore("1#1", Two("Aaa", a, T0));
        var pass = new ConvertPass(t.Install, new ConvertOptions());
        Assert.Equal(CharacterState.Rejected, pass.Run().Characters.Single().State);

        pass.Options = new ConvertOptions(KeepScreenshots: true);
        var later = T0.AddMinutes(5);
        var (black, white) = TestData.Pair(400, 1200, 150, 250, 100, 700);
        var kept = t.WriteShot(later, black);
        t.WriteShot(later.AddSeconds(1), white);
        t.WriteStore("1#12", Two("Bbb", b, later));
        // Unreadable now, its size and time unchanged: read again, it would not be "Rejected".
        var stamp = File.GetLastWriteTimeUtc(rejected);
        var bytes = File.ReadAllBytes(rejected);
        bytes[2] = 99;
        File.WriteAllBytes(rejected, bytes);
        File.SetLastWriteTimeUtc(rejected, stamp);

        var report = pass.Run();
        Assert.Equal(["Bbb"], report.Written);
        Assert.True(File.Exists(kept));
        Assert.Equal(0, report.BytesFreed);
        Assert.Equal("the two shots look identical (100% of the frame reads as opaque)",
            report.Characters.Single(c => c.Guid == a).Note);
    }

    [Fact]
    public void A_first_look_is_at_once_unless_paused()
    {
        using var t = new TempInstall();
        using var ran = new ManualResetEventSlim();
        using var w = new Watcher([(t.Install.Screenshots, "*.tga", false)], ran.Set,
            debounce: TimeSpan.FromMinutes(10), poll: TimeSpan.FromHours(1));
        w.TriggerNow();
        Assert.True(ran.Wait(TimeSpan.FromSeconds(10)), "no pass ran");

        ran.Reset();
        using var paused = new Watcher([(t.Install.Screenshots, "*.tga", false)], ran.Set,
            debounce: TimeSpan.FromMinutes(10), poll: TimeSpan.FromHours(1)) { Paused = true };
        paused.TriggerNow();
        Assert.False(ran.Wait(TimeSpan.FromMilliseconds(600)));
    }

    [Fact]
    public void Convert_now_runs_at_once_and_while_paused()
    {
        using var t = new TempInstall();
        using var ran = new ManualResetEventSlim();
        // A debounce so long that only a pass which skipped it can run in time.
        using var w = new Watcher([(t.Install.Screenshots, "*.tga", false)], ran.Set,
            debounce: TimeSpan.FromMinutes(10), poll: TimeSpan.FromHours(1)) { Paused = true };
        w.RunNow();
        Assert.True(ran.Wait(TimeSpan.FromSeconds(10)), "no pass ran");
    }

    [Fact]
    public void A_file_event_does_not_push_convert_now_back()
    {
        using var t = new TempInstall();
        using var ran = new ManualResetEventSlim();
        using var w = new Watcher([(t.Install.Screenshots, "*.tga", false)], ran.Set,
            debounce: TimeSpan.FromMinutes(10), poll: TimeSpan.FromHours(1));
        w.RunNow();
        w.Trigger();
        Assert.True(ran.Wait(TimeSpan.FromSeconds(10)), "the trigger postponed it");
    }

    [Fact]
    public void Convert_now_during_a_pass_runs_one_more_even_while_paused()
    {
        using var t = new TempInstall();
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var twice = new ManualResetEventSlim();
        var calls = 0;
        using var w = new Watcher([(t.Install.Screenshots, "*.tga", false)],
            () =>
            {
                if (Interlocked.Increment(ref calls) == 2) twice.Set();
                started.Set();
                release.Wait(TimeSpan.FromSeconds(10));
            },
            debounce: TimeSpan.FromMinutes(10), poll: TimeSpan.FromHours(1)) { Paused = true };

        w.RunNow();
        Assert.True(started.Wait(TimeSpan.FromSeconds(10)));
        w.RunNow();
        Thread.Sleep(200);
        release.Set();
        Assert.True(twice.Wait(TimeSpan.FromSeconds(10)), "the second request was dropped");
    }

    [Fact]
    public void Ending_a_pause_looks_at_what_happened_during_it()
    {
        using var t = new TempInstall();
        using var ran = new ManualResetEventSlim();
        using var w = new Watcher([(t.Install.Screenshots, "*.tga", false)], ran.Set,
            debounce: TimeSpan.FromMilliseconds(50), poll: TimeSpan.FromHours(1)) { Paused = true };
        File.WriteAllBytes(Path.Combine(t.Install.Screenshots, "WoWScrnShot_092926_010638.tga"), [1]);
        Assert.False(ran.Wait(TimeSpan.FromMilliseconds(500)));
        w.Paused = false;
        Assert.True(ran.Wait(TimeSpan.FromSeconds(10)), "nothing ran when the pause ended");
    }

    [Fact]
    public void Nothing_runs_once_the_watcher_is_disposed()
    {
        using var t = new TempInstall();
        using var ran = new ManualResetEventSlim();
        var w = new Watcher([(t.Install.Screenshots, "*.tga", false)], ran.Set,
            debounce: TimeSpan.FromMilliseconds(50), poll: TimeSpan.FromHours(1));
        w.Dispose();
        w.RunNow();
        w.Trigger();
        Assert.False(ran.Wait(TimeSpan.FromMilliseconds(600)));
    }

    [Fact]
    public void The_restart_notice_is_owed_per_install_and_survives_a_restart()
    {
        using var t = new TempInstall();
        var dir = Path.Combine(t.Root, "appdata");
        var s = new Settings().WithRestartNotice(@"C:\WoW\_classic_beta_", owed: true)
            .WithRestartNotice(@"D:\WoW\_classic_beta_", owed: true)
            .WithRestartNotice(@"c:\wow\_CLASSIC_BETA_", owed: true);          // the same folder
        Assert.Equal(2, s.RestartNoticeInstalls.Count);
        s.Save(dir);

        var back = Settings.Load(dir);
        Assert.Equal(s, back);
        Assert.True(back.OwesRestartNotice(@"C:\WOW\_classic_beta_"));
        Assert.False(back.OwesRestartNotice(@"E:\WoW\_classic_beta_"));

        back = back.WithRestartNotice(@"C:\WoW\_classic_beta_", owed: false);
        Assert.False(back.OwesRestartNotice(@"C:\WoW\_classic_beta_"));
        Assert.True(back.OwesRestartNotice(@"D:\WoW\_classic_beta_"));
        Assert.NotEqual(s, back);
    }

    [Fact]
    public void Settings_that_cannot_be_read_say_so_and_a_first_start_does_not()
    {
        using var t = new TempInstall();
        var dir = Path.Combine(t.Root, "appdata");
        Assert.Equal(new Settings(), Settings.Load(dir, out var problem));
        Assert.Null(problem);

        new Settings { WowFlavorDir = "X" }.Save(dir);
        Assert.Equal("X", Settings.Load(dir, out problem).WowFlavorDir);
        Assert.Null(problem);

        // Edited by hand, with a comma too many.
        File.WriteAllText(Path.Combine(dir, "settings.json"), "{ \"WowFlavorDir\": \"X\", }");
        Assert.Equal(new Settings(), Settings.Load(dir, out problem));
        Assert.Contains("settings.json", problem);
    }

    [Theory]
    [InlineData("""{ "RestartNoticeInstalls": null }""", 0)]
    [InlineData("""{ "RestartNoticeInstalls": [null, "", "C:\\WoW\\_classic_beta_"] }""", 1)]
    public void A_list_that_is_null_or_holds_one_is_no_reason_not_to_start(string json, int want)
    {
        using var t = new TempInstall();
        var dir = Path.Combine(t.Root, "appdata");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "settings.json"), json);

        var s = Settings.Load(dir, out var problem);
        Assert.Null(problem);
        Assert.Equal(want, s.RestartNoticeInstalls.Count);
        Assert.False(s.OwesRestartNotice(@"D:\x"));
        Assert.True(s.WithRestartNotice(@"D:\x", owed: true).OwesRestartNotice(@"D:\x"));
        Assert.Equal(s, Settings.Load(dir));
    }

    [Fact]
    public void Settings_written_before_the_notice_was_a_list_still_load()
    {
        using var t = new TempInstall();
        var dir = Path.Combine(t.Root, "appdata");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "settings.json"),
            """{ "WowFlavorDir": "X", "KeepScreenshots": true, "Paused": true, "FirstFolderNoticeShown": true }""");
        var s = Settings.Load(dir);
        Assert.Equal(new Settings { WowFlavorDir = "X", KeepScreenshots = true, Paused = true }, s);
        Assert.Empty(s.RestartNoticeInstalls);
    }
}
