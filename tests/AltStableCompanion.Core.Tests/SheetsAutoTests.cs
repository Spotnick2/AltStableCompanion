namespace AltStableCompanion.Core.Tests;

/// <summary>
/// The automatic export (#82, M2): N minutes after the game has closed and stayed closed, when
/// an account's AltStable.lua is newer than the last export. Run on the controller's own timer,
/// fast, with a clock and a game the test moves.
/// </summary>
public sealed class SheetsAutoTests
{
    private static readonly DateTime T0 = new(2026, 11, 20, 10, 0, 0);

    private sealed class World : IDisposable
    {
        public TempInstall T { get; }
        public FakeGoogleService Google { get; } = new();
        public Controller C { get; }
        public DateTime Now;
        public volatile bool Running;
        private readonly Lock _gate = new();

        public World(Func<Settings, Settings>? settings = null, Action<FakeGoogleService>? google = null)
        {
            T = SheetsControllerTests.Install(settings);
            Saved(T0);
            GoogleTokenStore.Save(SheetsControllerTests.Data(T), "refresh-1");
            google?.Invoke(Google);
            Now = T0;
            var hooks = new SheetHooks(new GoogleClient("the-client", "the-secret"), () => Google)
            {
                GameRunning = _ => Running,
                Tick = TimeSpan.FromMilliseconds(20),
            };
            C = new Controller(new StartupOptions(DataDir: SheetsControllerTests.Data(T)),
                () => throw new InvalidOperationException("detection was used"), clock: () => { lock (_gate) return Now; }, sheets: hooks);
            C.Start();
        }

        public void At(DateTime when) { lock (_gate) Now = when; }

        // The game writes the account's file (logout, /reload, exit) at this time.
        public void Saved(DateTime when) =>
            File.SetLastWriteTime(Path.Combine(T.Install.AccountsDir, "ACCOUNT1", "SavedVariables", SavedVariablesReader.FileName), when);

        public int Exports => Google.Count(":batchUpdate");

        // Long enough for many ticks: something that did not happen would have by now.
        public static void Ticks() => Thread.Sleep(250);

        public void Dispose()
        {
            C.Dispose();
            T.Dispose();
        }
    }

    [Fact]
    public void It_exports_once_the_game_has_been_closed_that_long_and_again_after_the_next_save()
    {
        using var w = new World();

        w.Running = true;
        w.At(T0.AddMinutes(1));
        World.Ticks();
        Assert.Equal(0, w.Exports);                       // playing

        w.Running = false;
        w.At(T0.AddMinutes(10));
        World.Ticks();
        Assert.Equal(0, w.Exports);                       // closed 9 minutes ago

        w.At(T0.AddMinutes(17));
        SheetsControllerTests.Until(() => w.Exports == 1, "the automatic export");
        Assert.StartsWith("Exported 2 characters", SheetsControllerTests.Exported(w.C));
        Assert.Equal(T0.ToUniversalTime(), Settings.Load(SheetsControllerTests.Data(w.T)).SheetExportedThrough);
        World.Ticks();
        Assert.Equal(1, w.Exports);                       // nothing new since

        w.Saved(T0.AddMinutes(20));                       // played again; closed at 20
        w.At(T0.AddMinutes(30));
        World.Ticks();
        Assert.Equal(1, w.Exports);
        w.At(T0.AddMinutes(36));
        SheetsControllerTests.Until(() => w.Exports == 2, "the export after the next session");
    }

    [Fact]
    public void Opening_the_game_again_starts_the_wait_over()
    {
        using var w = new World();

        w.At(T0.AddMinutes(10));
        World.Ticks();
        w.Running = true;                                 // the other account's client, before the 15 minutes
        w.At(T0.AddMinutes(14));
        World.Ticks();
        w.Running = false;
        w.At(T0.AddMinutes(20));
        World.Ticks();
        Assert.Equal(0, w.Exports);                       // 6 minutes since the game was last seen

        w.At(T0.AddMinutes(29));
        SheetsControllerTests.Until(() => w.Exports == 1, "the export 15 minutes after the last client closed");
    }

    [Fact]
    public void Pause_and_the_setting_hold_it_and_export_now_still_counts()
    {
        using var w = new World();

        w.C.SetPaused(true);
        w.At(T0.AddMinutes(60));
        World.Ticks();
        Assert.Equal(0, w.Exports);
        w.C.SetSheetAuto(false, 15);
        w.C.SetPaused(false);
        World.Ticks();
        Assert.Equal(0, w.Exports);
        Assert.False(Settings.Load(SheetsControllerTests.Data(w.T)).SheetAutoExport);

        // Export now covers the same save: turned back on, there is nothing new to export.
        w.C.ExportSheet();
        SheetsControllerTests.Until(() => w.Exports == 1, "Export now");
        SheetsControllerTests.Exported(w.C);
        w.C.SetSheetAuto(true, 15);
        World.Ticks();
        Assert.Equal(1, w.Exports);
    }

    [Fact]
    public void A_failed_automatic_export_waits_one_more_wait()
    {
        using var w = new World(s => s with { GoogleSheetId = "sheet-9", GoogleSheetUrl = GoogleSheets.UrlOf("sheet-9") },
            g =>
            {
                g.Sheets["sheet-9"] = [new GoogleSheets.Tab(1, "Characters", "Characters#1")];
                g.DriveRateLimited = true;
            });

        w.At(T0.AddMinutes(16));
        SheetsControllerTests.Until(() => w.Google.Count("drive/v3/files/sheet-9") == 1, "the first try");
        World.Ticks();
        Assert.Equal(1, w.Google.Count("drive/v3/files/sheet-9"));
        Assert.Null(Settings.Load(SheetsControllerTests.Data(w.T)).SheetExportedThrough);

        w.Google.DriveRateLimited = false;
        w.At(T0.AddMinutes(32));
        SheetsControllerTests.Until(() => w.Exports == 1, "the try one wait later");
    }

    [Fact]
    public void A_save_made_while_an_export_runs_is_exported_next_time()
    {
        using var w = new World(google: g => g.HoldListing = new SemaphoreSlim(0));

        w.At(T0.AddMinutes(16));
        SheetsControllerTests.Until(() => w.Google.Count("GET sheets.googleapis.com") + w.Google.Count("POST sheets.googleapis.com/v4/spreadsheets") >= 1,
            "the automatic export under way");
        w.Saved(T0.AddMinutes(17));                       // the game wrote meanwhile
        w.Google.HoldListing!.Release(10);
        SheetsControllerTests.Until(() => w.Exports == 1, "the first export");
        SheetsControllerTests.Exported(w.C);              // its end: the save time is kept after Google's answer
        Assert.Equal(T0.ToUniversalTime(), Settings.Load(SheetsControllerTests.Data(w.T)).SheetExportedThrough);

        w.At(T0.AddMinutes(33));
        SheetsControllerTests.Until(() => w.Exports == 2, "the save it did not read, exported");
    }

    [Fact]
    public void A_save_after_the_autumn_clock_change_is_newer_than_one_before_it()
    {
        // Measured in UTC: 01:40 local before the change is later on the clock than 01:10 after it.
        var before = new DateTime(2026, 11, 1, 5, 40, 0, DateTimeKind.Utc);
        var after = new DateTime(2026, 11, 1, 6, 10, 0, DateTimeKind.Utc);
        using var w = new World(s => s with { SheetExportedThrough = before });
        File.SetLastWriteTimeUtc(Path.Combine(w.T.Install.AccountsDir, "ACCOUNT1", "SavedVariables", SavedVariablesReader.FileName), after);

        w.At(after.AddMinutes(16).ToLocalTime());
        SheetsControllerTests.Until(() => w.Exports == 1, "the save after the change, exported");
    }

    [Fact]
    public void An_automatic_export_found_due_is_not_started_once_it_no_longer_is()
    {
        using var w = new World();
        // Paused before the clock passes the wait, so the timer itself never finds it due.
        w.C.SetPaused(true);
        w.At(T0.AddMinutes(16));
        // The tick saw an export due; the player paused before it was started.
        w.C.StartAutoForTest(T0.ToUniversalTime());
        World.Ticks();
        Assert.Equal(0, w.Exports);
        // Or turned the setting off - while still paused, so the timer has no window either.
        w.C.SetSheetAuto(false, 15);
        w.C.SetPaused(false);
        w.C.StartAutoForTest(T0.ToUniversalTime());
        Thread.Sleep(100);
        Assert.Equal(0, w.Exports);
        Assert.False(w.C.Current.Sheets.Exporting);
        // Export now is not held by any of it.
        w.C.SetPaused(true);
        w.C.ExportSheet();
        SheetsControllerTests.Until(() => w.Exports == 1, "Export now");
    }

    [Fact]
    public void A_delay_raised_while_a_tick_looked_is_the_one_that_counts()
    {
        using var w = new World(s => s with { SheetAutoMinutes = 5 });
        w.C.SetSheetAuto(true, 120);                      // raised before the tick's finding is claimed
        w.At(T0.AddMinutes(16));

        // The tick found it due by the old 5 minutes; by 120 it is not.
        w.C.StartAutoForTest(T0.ToUniversalTime());
        Thread.Sleep(100);
        Assert.Equal(0, w.Exports);
        // Nor when the game was seen again after the quiet the tick counted from. The game is
        // running, and the timer has seen it, before the delay is lowered, so the timer itself
        // never finds it due in between.
        w.Running = true;
        SheetsControllerTests.Until(() => w.C.GameSeenForTest is not null, "the timer seeing the game");
        w.C.SetSheetAuto(true, 5);
        w.Running = false;
        w.C.StartAutoForTest(T0.ToUniversalTime());
        Thread.Sleep(100);
        Assert.Equal(0, w.Exports);
    }

    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]     // the folder's ACL; the app is Windows-only
    public void A_scan_that_fails_ends_the_export_instead_of_leaving_it_running()
    {
        using var w = new World();
        // The account's folder unreadable for this user: listing it throws.
        var saved = new DirectoryInfo(Path.Combine(w.T.Install.AccountsDir, "ACCOUNT1", "SavedVariables"));
        var me = System.Security.Principal.WindowsIdentity.GetCurrent().User!;
        var deny = new System.Security.AccessControl.FileSystemAccessRule(me,
            System.Security.AccessControl.FileSystemRights.ListDirectory, System.Security.AccessControl.AccessControlType.Deny);
        var acl = saved.GetAccessControl();
        acl.AddAccessRule(deny);
        saved.SetAccessControl(acl);
        try
        {
            w.C.ExportSheet();
            var note = SheetsControllerTests.Exported(w.C);

            Assert.False(w.C.Current.Sheets.Exporting);
            Assert.True(w.C.Current.Sheets.Problem, note);
        }
        finally
        {
            acl.RemoveAccessRule(deny);
            saved.SetAccessControl(acl);
        }
        // And the next one goes ahead.
        w.C.ExportSheet();
        SheetsControllerTests.Until(() => w.Exports == 1, "the export once the folder reads again");
    }

    [Fact]
    public void The_theme_is_kept_and_the_next_export_uses_it()
    {
        using var w = new World();

        w.C.SetSheetTheme("light");
        w.C.ExportSheet();
        SheetsControllerTests.Until(() => w.Exports == 1, "the export");

        Assert.Equal(SheetPalette.LightName, Settings.Load(SheetsControllerTests.Data(w.T)).SheetTheme);
        Assert.Equal(SheetPalette.LightName, w.C.Current.Sheets.Theme);
        // The masthead's colour, as the batch writes it.
        var cells = w.Google.Batches.Single().Select(r => r!["updateCells"]).OfType<System.Text.Json.Nodes.JsonNode>().First();
        var red = (double)cells["rows"]![0]!["values"]![1]!["userEnteredFormat"]!["backgroundColor"]!["red"]!;
        Assert.Equal(Convert.ToInt32(SheetPalette.Light.Masthead[..2], 16) / 255.0, red, 9);
        w.C.SetSheetTheme("anything");
        Assert.Equal(SheetPalette.DarkName, w.C.Current.Sheets.Theme);
    }

    [Fact]
    public void The_minutes_are_five_to_a_hundred_and_twenty()
    {
        using var w = new World();

        w.C.SetSheetAuto(true, 500);
        Assert.Equal(Settings.MaxSheetAutoMinutes, w.C.Current.Sheets.AutoMinutes);
        Assert.Equal(Settings.MaxSheetAutoMinutes, Settings.Load(SheetsControllerTests.Data(w.T)).SheetAutoMinutes);
        w.C.SetSheetAuto(true, 1);
        Assert.Equal(Settings.MinSheetAutoMinutes, w.C.Current.Sheets.AutoMinutes);
        Assert.True(new Settings().SheetAutoExport);       // on unless turned off
    }

    [Fact]
    public void No_game_runs_from_a_folder_with_no_game_in_it()
    {
        using var t = new TempInstall();

        Assert.False(WowProcess.Running(t.Install));
    }
}
