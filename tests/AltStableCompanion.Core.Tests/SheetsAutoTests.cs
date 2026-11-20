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
        Assert.Equal(T0, Settings.Load(SheetsControllerTests.Data(w.T)).SheetExportedThrough);
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
        Assert.Equal(T0, Settings.Load(SheetsControllerTests.Data(w.T)).SheetExportedThrough);

        w.At(T0.AddMinutes(33));
        SheetsControllerTests.Until(() => w.Exports == 2, "the save it did not read, exported");
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
