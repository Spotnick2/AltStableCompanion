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

    private static PortraitRow Ready(string name = "Aaa") =>
        new(name, "Player-1-" + name, name.ToLowerInvariant() + ".tga", PortraitSource.ByGuid, T0, T0, CaptureOutcome.Converted, null);

    private static PortraitRow Rejected(string name = "Bad") =>
        new(name, "Player-1-" + name, null, PortraitSource.None, null, T0, CaptureOutcome.Unusable, "identical shots");

    private static Headline Head(ShellState s) => PassText.Headline(s);

    [Fact]
    public void The_headline_is_the_first_thing_that_applies()
    {
        using var t = new TempInstall();
        var wrote = new PassNote(T0, ["Aaa"]);
        // Everything at once. Each line below takes away what outranked the rest.
        var all = With(t, Report(stale: true, refused: 1, accounts: 0) with
        {
            Portraits = [Ready(), Rejected()], ManifestWritten = false,
        }, addon: false) with
        {
            Paused = true, Converting = true, Stopping = true, LastError = "boom", FirstStart = true,
            LastWritten = wrote,
        };

        Assert.Equal("Finishing...", Head(all).Title);
        all = all with { Stopping = false };
        Assert.Equal("Ready to start", Head(all).Title);
        all = all with { FirstStart = false };
        // A check in hand outranks the failure of the one before it.
        Assert.Equal(new Headline("Checking...", "The check before this one failed.", HeadlineKind.Busy), Head(all));
        Assert.Null(Head(all with { LastError = null }).Next);
        all = all with { Converting = false };
        Assert.Equal("The last check failed", Head(all).Title);
        Assert.Contains("last check that worked", Head(all).Next);
        Assert.DoesNotContain("last check that worked", Head(all with { Report = null }).Next);
        all = all with { LastError = null };
        Assert.Equal("Portraits written, but not listed", Head(all).Title);
        all = all with { Report = all.Report! with { ManifestWritten = true } };
        Assert.Equal("AltStable is not installed in this game", Head(all).Title);
        all = all with { Install = Installed(t, addon: true) };
        Assert.Equal("This app is too old for your AltStable", Head(all).Title);
        all = all with { Report = all.Report! with { Refused = 0 } };
        Assert.Equal(new Headline("Portrait written: Aaa", "If WoW is open, /reload to load it.", HeadlineKind.Info, Dismissable: true), Head(all));
        // A new addon folder: a reload will not find it, and the headline must not say so.
        Assert.Equal("Restart WoW once to see it: see below.", PassText.Headline(all, restartNotice: true).Next);
        all = all with { UpdateSeen = true };
        Assert.Equal(new Headline("1 character needs attention", "See the list.", HeadlineKind.Attention), Head(all));
        all = all with { Report = all.Report! with { Portraits = [Ready()] } };
        Assert.Equal("No AltStable data yet", Head(all).Title);
        all = all with { Report = all.Report! with { Accounts = 1 } };
        Assert.Equal("A capture may be waiting", Head(all).Title);
        all = all with { Report = all.Report! with { Stale = null } };
        Assert.Equal("Your portraits are ready", Head(all).Title);
        Assert.Equal(HeadlineKind.Good, Head(all).Kind);
    }

    [Fact]
    public void What_was_written_is_said_with_its_count()
    {
        using var t = new TempInstall();
        var s = With(t, Report() with { Portraits = [Ready()] }) with { LastWritten = new PassNote(T0, ["a", "b", "c"]) };
        Assert.Equal(new Headline("3 portraits written", "If WoW is open, /reload to load them.", HeadlineKind.Info, Dismissable: true), Head(s));
        // It is advice, and only advice: the app has not seen what the game loaded.
        Assert.StartsWith("If WoW is open", Head(s).Next);
    }

    [Fact]
    public void Nothing_is_called_up_to_date_and_a_pause_is_never_promised_away()
    {
        using var t = new TempInstall();
        var s = With(t, Report() with { Portraits = [Ready()] });
        Assert.Equal(new Headline("Your portraits are ready", "New captures are processed automatically while this app runs.", HeadlineKind.Good), Head(s));
        // Off is said once, by the marker under the headline: the next step does not promise
        // what will not happen, and does not repeat the marker either.
        Assert.Equal(new Headline("Your portraits are ready", null, HeadlineKind.Good), Head(s with { Paused = true }));
        Assert.DoesNotContain("up to date", Head(s).Title);
        // A screenshot still being written is not a portrait that is ready - with a portrait
        // already there, and without one.
        var writing = Ready() with { Outcome = CaptureOutcome.Writing, Note = "a screenshot is in use - next pass" };
        var expected = new Headline("A capture is still being written", "The next check looks at it again.", HeadlineKind.Info);
        Assert.Equal(expected, Head(With(t, Report() with { Portraits = [Ready(), writing] })));
        Assert.Equal(expected, Head(With(t, Report() with { Portraits = [Ready(), writing with { Source = PortraitSource.None, FileName = null }] })));
    }

    [Fact]
    public void Before_the_first_check_the_headline_says_so()
    {
        using var t = new TempInstall();
        Assert.Equal(new Headline("Not checked yet", null, HeadlineKind.Busy), Head(With(t)));
        Assert.Equal(new Headline("Not checked yet", null, HeadlineKind.Info), Head(With(t) with { Paused = true }));
    }

    [Fact]
    public void With_nothing_at_all_the_next_step_is_the_first_capture()
    {
        using var t = new TempInstall();
        Assert.Equal(new Headline("No portraits yet", "In WoW: /alts portrait, then /reload.", HeadlineKind.Info),
            Head(With(t, Report() with { Portraits = [] })));
    }

    private static readonly DateTime At = new(2026, 9, 29, 12, 31, 5);

    private static string? Activity(PassNote? last, PassNote? written = null, DateTime? today = null) =>
        PassText.Activity(new ShellState(LastPass: last, LastWritten: written), today ?? At);

    [Fact]
    public void A_pass_that_found_nothing_still_shows_that_it_ran()
    {
        Assert.Null(Activity(null));
        Assert.Equal("Last check today 12:31:05: nothing new.", Activity(new PassNote(At, [])));
        Assert.Equal("Last check today 12:31:05: wrote Name 1.", Activity(new PassNote(At, ["Name 1"])));
        Assert.Equal("Last check today 12:31:05: wrote 3 portraits.", Activity(new PassNote(At, ["a", "b", "c"])));
        Assert.Equal("Last check today 12:31:05: the pass failed.", Activity(new PassNote(At, [], Failed: true)));
    }

    [Fact]
    public void Nothing_new_is_not_said_of_a_capture_that_could_not_be_converted()
    {
        Assert.Equal("Last check today 12:31:05: nothing written; 1 capture could not be converted - see the list.",
            Activity(new PassNote(At, [], Unconverted: 1)));
        Assert.Equal("Last check today 12:31:05: nothing written; 2 captures could not be converted - see the list.",
            Activity(new PassNote(At, [], Unconverted: 2, Waiting: true)));
        Assert.Equal("Last check today 12:31:05: wrote Aaa; 1 capture could not be converted - see the list.",
            Activity(new PassNote(At, ["Aaa"], Unconverted: 1)));
        // Under "Newer screenshots found ...": nothing NEW would contradict the line above it.
        Assert.Equal("Last check today 12:31:05: nothing to convert yet.", Activity(new PassNote(At, [], Waiting: true)));
    }

    [Fact]
    public void What_a_pass_did_is_counted_from_its_own_report()
    {
        static CharacterStatus Is(CharacterState s) => new("g", "n", At, s, null);
        var report = new PassReport(
            [.. Enum.GetValues<CharacterState>().Select(Is)], ["Aaa"], 0, (At, null), false, []);
        Assert.Equal(new PassNote(At, ["Aaa"], Unconverted: 4, Waiting: true) with { Written = report.Written },
            PassNote.Of(report, At));
        // Portrait and Missing are not "looked at and could not be used".
        Assert.Equal(0, PassNote.Of(report with { Characters = [Is(CharacterState.Portrait), Is(CharacterState.Missing)] }, At).Unconverted);
        Assert.False(PassNote.Of(report with { Stale = null }, At).Waiting);

        var failed = PassNote.Of(null, At);
        Assert.True(failed.Failed);
        Assert.Empty(failed.Written);
    }

    [Fact]
    public void The_last_portrait_written_stays_in_view_when_later_passes_find_nothing()
    {
        var wrote = new PassNote(new DateTime(2026, 9, 29, 12, 13, 54), ["Karuzo Macphisto"]);
        Assert.Equal("Last check today 12:31:05: nothing new. Last written: Karuzo Macphisto, today 12:13:54.",
            Activity(new PassNote(At, []), wrote));
        // Also after a pass that failed: what the pass BEFORE it wrote decides nothing.
        Assert.Equal("Last check today 12:31:05: the pass failed. Last written: Karuzo Macphisto, today 12:13:54.",
            Activity(new PassNote(At, [], Failed: true), wrote));
        // The pass that writes says it once, not twice.
        Assert.Equal("Last check today 12:13:54: wrote Karuzo Macphisto.", Activity(wrote, wrote));
    }

    [Fact]
    public void A_time_that_is_not_today_says_which_day()
    {
        var monday = new PassNote(new DateTime(2026, 9, 28, 23, 50, 0), ["Aaa"]);
        var wednesday = new DateTime(2026, 9, 30, 9, 12, 5);
        Assert.Equal("Last check today 09:12:05: nothing new. Last written: Aaa, 2026-09-28 23:50:00.",
            Activity(new PassNote(wednesday, []), monday, today: wednesday));
        // Paused since Monday: the check is old too. And yesterday is called by name.
        Assert.Equal("Last check 2026-09-28 23:50:00: wrote Aaa.", Activity(monday, monday, today: wednesday));
        Assert.Equal("Last check yesterday 23:50:00: wrote Aaa.", Activity(monday, monday, today: wednesday.AddDays(-1)));
    }

    [Fact]
    public void Without_an_install_the_headline_says_why_and_where_to_choose_one()
    {
        Assert.Equal(new Headline("World of Warcraft folder not set", "Couldn't find the WoW folder. Choose it in Settings.", HeadlineKind.Problem),
            Head(new ShellState()));
        Assert.Equal("It is gone: X. Choose it in Settings.",
            Head(new ShellState(InstallProblem: "It is gone: X", Converting: true, FirstStart: true)).Next);
    }

    [Fact]
    public void The_reload_hint_is_a_hint()
    {
        // Any screenshot newer than the records raises it, a hand-taken one included: it must
        // not tell everybody to reload.
        using var t = new TempInstall();
        var head = Head(With(t, Report(stale: true) with { Portraits = [Ready()] }));
        Assert.StartsWith("If you just captured", head.Next);
        Assert.Contains("may be", head.Title);
    }

    [Fact]
    public void The_count_is_of_portraits_and_what_needs_attention_is_said_beside_it()
    {
        Assert.Equal("0", PassText.Count([]));
        Assert.Equal("1", PassText.Count([Ready()]));
        Assert.Equal("2", PassText.Count([Ready("A"), Ready("B")]));
        Assert.Null(PassText.Attention([Ready()]));
        // A capture with no portrait is not a portrait.
        // Characters, not portraits: the count of files and the count of characters are not
        // one inside the other.
        Assert.Equal("1", PassText.Count([Ready(), Rejected()]));
        Assert.Equal("1 character needs attention", PassText.Attention([Ready(), Rejected()]));
        Assert.Equal("0", PassText.Count([Rejected("A"), Rejected("B")]));
        Assert.Equal("2 characters need attention", PassText.Attention([Rejected("A"), Rejected("B")]));
        // Two namesakes on one legacy file: one portrait on disk.
        Assert.Equal("1", PassText.Count([Ready("A") with { FileName = "twin.tga" }, Ready("B") with { FileName = "twin.tga" }]));
    }

    [Fact]
    public void A_row_says_the_portrait_s_time_and_the_capture_s_time_apart()
    {
        var today = new DateTime(2026, 9, 29, 18, 0, 0);
        var captured = new DateTime(2026, 9, 29, 12, 13, 0);
        var older = new DateTime(2026, 9, 28, 9, 5, 0);
        var old = new DateTime(2026, 9, 26, 1, 50, 0);
        PortraitRow Row(PortraitSource source, CaptureOutcome outcome, string? note = null, DateTime? modified = null) =>
            new("Aaa", "Player-1-AAAA", source == PortraitSource.None ? null : "aaa.tga", source,
                source == PortraitSource.None ? null : modified ?? captured, captured, outcome, note);

        Assert.Equal("Ready", PassText.RowState(Row(PortraitSource.ByGuid, CaptureOutcome.Converted)));
        Assert.Equal("No portrait", PassText.RowState(Row(PortraitSource.None, CaptureOutcome.Unusable)));

        Assert.Equal("latest capture today 12:13 · converted",
            PassText.RowDetail(Row(PortraitSource.ByGuid, CaptureOutcome.Converted), today));
        // An older portrait, a newer capture that was rejected: two facts, two times.
        Assert.Equal("latest capture today 12:13 · identical shots · file modified yesterday 09:05",
            PassText.RowDetail(Row(PortraitSource.ByGuid, CaptureOutcome.Unusable, "identical shots", older), today));
        Assert.Equal("latest capture today 12:13 · not converted: its screenshots are gone · file modified 2026-09-26 01:50",
            PassText.RowDetail(Row(PortraitSource.ByGuid, CaptureOutcome.NoScreenshots, "no screenshots for this capture on disk", old), today));
        Assert.Equal("latest capture today 12:13 · no screenshots for this capture on disk - capture again if they are gone",
            PassText.RowDetail(Row(PortraitSource.None, CaptureOutcome.NoScreenshots, "no screenshots for this capture on disk"), today));
        Assert.Equal("latest capture today 12:13 · a screenshot is in use - next pass",
            PassText.RowDetail(Row(PortraitSource.None, CaptureOutcome.Writing, "a screenshot is in use - next pass"), today));
        Assert.Equal("latest capture today 12:13 · portrait from an earlier converter · file modified 2026-09-26 01:50",
            PassText.RowDetail(Row(PortraitSource.ByGuid, CaptureOutcome.Unknown, null, old), today));
        Assert.Equal("latest capture today 12:13 · not converted: its screenshots are gone · portrait found by name (aaa.tga) · file modified 2026-09-26 01:50",
            PassText.RowDetail(Row(PortraitSource.ByName, CaptureOutcome.NoScreenshots, null, old), today));

        // A file nobody's capture resolved to: its name, and when it was written.
        Assert.Equal("karuzo-elegia.tga · file modified 2026-09-26 01:50", PassText.RowDetail(
            new PortraitRow("Karuzo Elegia", null, "karuzo-elegia.tga", PortraitSource.File, old, null, CaptureOutcome.None, null), today));

        Assert.EndsWith("· nearly square: something else may be in it - capture again",
            PassText.RowDetail(Row(PortraitSource.ByGuid, CaptureOutcome.Converted) with { NearlySquare = true }, today));
        Assert.EndsWith("· Player-1-AAAA",
            PassText.RowDetail(Row(PortraitSource.ByGuid, CaptureOutcome.Converted) with { ShowGuid = true }, today));
    }

    [Fact]
    public void The_search_box_narrows_the_list_by_name_and_nothing_narrows_nothing()
    {
        var rows = new[] { Ready("Kaleid Sumner"), Ready("Karuzo Elegia"), Rejected("Zoru") };
        Assert.Same(rows, PassText.Matching(rows, null));
        Assert.Same(rows, PassText.Matching(rows, "  "));
        Assert.Equal(["Kaleid Sumner", "Karuzo Elegia"], PassText.Matching(rows, "ka").Select(r => r.Name));
        Assert.Equal(["Karuzo Elegia"], PassText.Matching(rows, " ELEG ").Select(r => r.Name));
        Assert.Equal(["Zoru"], PassText.Matching(rows, "zor").Select(r => r.Name));
        Assert.Empty(PassText.Matching(rows, "nobody"));
    }

    [Fact]
    public void A_row_with_nothing_to_act_on_says_only_when_and_what_that_time_is()
    {
        var today = new DateTime(2026, 9, 29, 18, 0, 0);
        var captured = new DateTime(2026, 9, 29, 12, 13, 0);
        var old = new DateTime(2026, 9, 26, 1, 50, 0);
        PortraitRow Row(PortraitSource source, CaptureOutcome outcome, string? note = null, DateTime? modified = null) =>
            new("Aaa", "Player-1-AAAA", source == PortraitSource.None ? null : "aaa.tga", source,
                source == PortraitSource.None ? null : modified ?? captured, captured, outcome, note);

        // The capture's time is the capture's, the file's time is the file's: neither is "updated".
        Assert.Equal("Captured today 12:13", PassText.RowSummary(Row(PortraitSource.ByGuid, CaptureOutcome.Converted), today));
        Assert.Equal("Updated 2026-09-26 01:50", PassText.RowSummary(Row(PortraitSource.ByGuid, CaptureOutcome.Unknown, null, old), today));
        // An older portrait whose newer capture is gone: the portrait's time, not the capture's.
        Assert.Equal("Updated 2026-09-26 01:50",
            PassText.RowSummary(Row(PortraitSource.ByGuid, CaptureOutcome.NoScreenshots, "no screenshots for this capture on disk", old), today));
        Assert.Equal("Updated 2026-09-26 01:50", PassText.RowSummary(
            new PortraitRow("Karuzo Elegia", null, "karuzo-elegia.tga", PortraitSource.File, old, null, CaptureOutcome.None, null), today));

        // The lines that are the thing to read stay whole: attention, a screenshot on its way,
        // and a name two characters share.
        Assert.Null(PassText.RowSummary(Row(PortraitSource.ByGuid, CaptureOutcome.Unusable, "identical shots", old), today));
        Assert.Null(PassText.RowSummary(Row(PortraitSource.None, CaptureOutcome.Writing, "a screenshot is in use - next pass"), today));
        Assert.Null(PassText.RowSummary(Row(PortraitSource.ByGuid, CaptureOutcome.Converted) with { ShowGuid = true }, today));

        // The tooltip: the detail when the line is not already it; the word about the
        // picture when it could not be read - alone when the line says everything else.
        Assert.Null(PassText.RowTooltip(null, "the detail", previewFailed: false));
        Assert.Equal("the detail", PassText.RowTooltip("Captured today 12:13", "the detail", previewFailed: false));
        Assert.Equal("the detail · " + PassText.PreviewUnavailable, PassText.RowTooltip("Captured today 12:13", "the detail", previewFailed: true));
        Assert.Equal(PassText.PreviewUnavailable, PassText.RowTooltip(null, "the detail", previewFailed: true));
    }

    [Fact]
    public void The_tray_says_in_a_word_what_the_app_is_doing()
    {
        using var t = new TempInstall();
        var s = new ShellState(Install: t.Install, Paused: true, Converting: true, Stopping: true);
        Assert.Equal("AltStable Companion - finishing", PassText.TrayTip(s));
        Assert.Equal("AltStable Companion - converting", PassText.TrayTip(s with { Stopping = false }));
        Assert.Equal("AltStable Companion - automatic processing off", PassText.TrayTip(s with { Stopping = false, Converting = false }));
        Assert.Equal("AltStable Companion - watching", PassText.TrayTip(new ShellState(Install: t.Install)));
        Assert.Equal("AltStable Companion - no WoW folder", PassText.TrayTip(new ShellState(Converting: true)));
        Assert.Equal("AltStable Companion - not started: open the window", PassText.TrayTip(new ShellState(Install: t.Install, FirstStart: true)));
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
    public void Only_forever_is_the_flavour_the_cutouts_are_written_for()
    {
        Assert.Null(PassText.FlavorWarning(new WowInstall(@"C:\WoW\_classic_beta_")));
        Assert.Contains("_retail_", PassText.FlavorWarning(new WowInstall(@"C:\WoW\_retail_")));
    }

    [Fact]
    public void The_install_line_names_the_game_the_addon_and_the_accounts()
    {
        using var t = new TempInstall();
        Assert.Equal(("World of Warcraft Forever", "AltStable not installed", null), PassText.InstallSummary(t.Install, null));
        var install = Installed(t, addon: true);
        Assert.Equal(("World of Warcraft Forever", "AltStable detected", null), PassText.InstallSummary(install, null));
        Assert.Equal("1 account", PassText.InstallSummary(install, Report(accounts: 1)).Accounts);
        Assert.Equal("2 accounts", PassText.InstallSummary(install, Report(accounts: 2)).Accounts);
        Assert.Equal("World of Warcraft (_retail_)", PassText.InstallSummary(new WowInstall(@"C:\WoW\_retail_"), null).Game);
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

    [Theory]
    [InlineData(null, "clear")]
    [InlineData("", "clear")]
    [InlineData("clear", "clear")]
    [InlineData(" Smoked ", "smoked")]
    [InlineData("FLAT", "flat")]
    [InlineData("liquid", "clear")]
    public void A_skin_is_one_of_the_addon_s_or_the_default(string? name, string want)
    {
        Assert.Equal(want, Skins.Normalize(name));
        Assert.Equal(want, new Settings { Skin = name! }.Skin);
    }

    [Fact]
    public void The_skin_is_saved_and_told_apart()
    {
        using var t = new TempInstall();
        var dir = Path.Combine(t.Root, "appdata");
        new Settings { Skin = Skins.Smoked }.Save(dir);
        Assert.Equal(Skins.Smoked, Settings.Load(dir).Skin);
        Assert.NotEqual(new Settings { Skin = Skins.Smoked }, new Settings { Skin = Skins.Flat });

        File.WriteAllText(Path.Combine(dir, "settings.json"), """{ "Skin": null }""");
        Assert.Equal(Skins.Clear, Settings.Load(dir, out var problem).Skin);
        Assert.Null(problem);
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
        // A folder chosen by a version with no Started: whoever did has used the app.
        Assert.Equal(new Settings { WowFlavorDir = "X", KeepScreenshots = true, Paused = true, Started = true }, s);
        Assert.Empty(s.RestartNoticeInstalls);

        // A file this version wrote before the card was answered says so, and is believed.
        File.WriteAllText(Path.Combine(dir, "settings.json"), """{ "WowFlavorDir": "X", "Started": false }""");
        Assert.False(Settings.Load(dir).Started);
        // Nothing chosen, nothing owed: a first start either way.
        File.WriteAllText(Path.Combine(dir, "settings.json"), """{ "Paused": true }""");
        Assert.False(Settings.Load(dir).Started);
    }
}
