using AltStableCompanion.Core;

namespace AltStableCompanion.Core.Tests;

public class DiagnosticsTests
{
    private static readonly DateTime T0 = new(2026, 9, 29, 1, 6, 38);
    private const string Profile = @"C:\Users\Somebody";

    // Two accounts with a capture each, a log that names them both - and a third account, of
    // another install, that only the log names - and the profile folder.
    private static (TempInstall T, DiagnosticsFacts Facts) Setup()
    {
        var t = new TempInstall();
        string[] accounts = ["SECRETACCT", "12345678#1"];
        foreach (var (account, i) in accounts.Select((a, i) => (a, i)))
        {
            var at = T0.AddMinutes(i);
            t.WriteStore(account, TestData.SavedVariables(
                [TestData.Record($"Alt{i}", $"Player-1-0000000{i}", 1, at), TestData.Record($"Alt{i}", $"Player-1-0000000{i}", 2, at.AddSeconds(1))]));
        }
        var log = Path.Combine(t.Root, "appdata", "log.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(log)!);
        File.WriteAllLines(log,
        [
            $@"2026-09-29 01:06:40  skipped {t.Install.AccountsDir}\SECRETACCT\SavedVariables\AltStable.lua this pass: in use",
            @"2026-09-29 01:06:41  skipped D:\Games\WoW\_classic_beta_\WTF\Account\OTHERACCT\SavedVariables\AltStable.lua",
            "2026-09-29 01:06:42  read 12345678#1's store, and secretacct's",
            $@"2026-09-29 01:06:43  settings in {Profile}\AppData\Roaming\AltStableCompanion; not {Profile}2\x",
        ]);
        return (t, new DiagnosticsFacts("0.1.0-beta.2+abc1234", t.Install, "detected", Enhance: false,
            CodexProbed: true, CodexStatus: null, log));
    }

    [Fact]
    public void No_account_folder_name_and_no_profile_path_is_in_the_file()
    {
        var (t, facts) = Setup();
        using var _ = t;

        var text = Diagnostics.Build(facts, T0, Profile);

        foreach (var raw in new[] { "SECRETACCT", "12345678#1", "OTHERACCT", Profile + @"\" })
        {
            Assert.DoesNotContain(raw, text, StringComparison.OrdinalIgnoreCase);
        }
        // Sorted: 12345678#1 is the first account, SECRETACCT the second; the log's other one, third.
        Assert.Contains(@"WTF\Account\Account2\SavedVariables", text);
        Assert.Contains(@"WTF\Account\Account3\SavedVariables", text);
        Assert.Contains("read Account1's store, and Account2's", text);
        Assert.Contains(@"settings in %USERPROFILE%\AppData\Roaming", text);
        // A folder that only begins like the profile is somebody else's.
        Assert.Contains(Profile + @"2\x", text);
    }

    [Fact]
    public void The_file_has_the_versions_and_each_characters_counts()
    {
        var (t, facts) = Setup();
        using var _ = t;
        Directory.CreateDirectory(Path.Combine(t.Install.AddOnsDir, "AltStable"));
        File.WriteAllText(Path.Combine(t.Install.AddOnsDir, "AltStable", "AltStable.toc"), "## Interface: 11508\n## Version: 2.4.0\n");

        var text = Diagnostics.Build(facts, T0, Profile);

        Assert.Contains("AltStable:  2.4.0", text);
        Assert.Contains("Roster:     not installed", text);
        Assert.Contains("Codex:      not found", text);
        Assert.Contains("Alt0 (Player-1-00000000): captures 1, cutouts 0, enhanced 0", text);
        Assert.Contains("Alt1 (Player-1-00000001): captures 1, cutouts 0, enhanced 0", text);
        Assert.Contains("Accounts:   2, with AltStable.lua: 2, capture records: 2", text);
    }

    [Fact]
    public void A_renamed_character_goes_by_its_current_name()
    {
        using var t = new TempInstall();
        const string guid = "Player-1-0000000A";
        t.WriteStore("1#1", TestData.SavedVariables(
        [
            TestData.Record("Before", guid, 1, T0), TestData.Record("Before", guid, 2, T0.AddSeconds(1)),
            TestData.Record("After", guid, 1, T0.AddHours(1)), TestData.Record("After", guid, 2, T0.AddHours(1).AddSeconds(1)),
        ]));
        var facts = new DiagnosticsFacts("0.1.0", t.Install, "detected", false, false, null, Path.Combine(t.Root, "log.txt"));

        var text = Diagnostics.Build(facts, T0, Profile);

        Assert.Contains($"After ({guid}): captures 2, cutouts 0, enhanced 0", text);
        Assert.DoesNotContain("Before", text);
    }

    [Fact]
    public void Cutouts_and_enhanced_pictures_are_counted_by_whose_they_are()
    {
        // Alt0 has two cutouts (a rename leaves the old file) and one enhanced picture; a
        // cutout from before sidecars has no GUID, and is a line of its own.
        var (t, facts) = Setup();
        using var _ = t;
        var f = new CutoutFolder(t.Install.CutoutAddonDir);
        var meta = new CutoutMeta { W = 3, H = 4, TexW = 4, TexH = 4, Guid = "Player-1-00000000", Epoch = 1 };
        f.WriteCutout("alt0", TestData.Solid(4, 4, 1, 2, 3), meta);
        f.WriteCutout("old-name", TestData.Solid(4, 4, 1, 2, 3), meta with { Epoch = 0 });
        f.WriteEnhanced("alt0", TestData.Solid(4, 4, 1, 2, 3), meta);
        TgaCodec.Write(Path.Combine(f.CutoutsDir, "no-sidecar.tga"), TestData.Solid(4, 4, 1, 2, 3));

        var text = Diagnostics.Build(facts, T0, Profile);

        Assert.Contains("Alt0 (Player-1-00000000): captures 1, cutouts 2, enhanced 1", text);
        Assert.Contains("Alt1 (Player-1-00000001): captures 1, cutouts 0, enhanced 0", text);
        Assert.Contains("No Sidecar (file no-sidecar): captures 0, cutouts 1, enhanced 0", text);
    }

    [Fact]
    public void The_log_tail_reaches_into_the_old_file_and_stops_at_the_count()
    {
        using var t = new TempInstall();
        var log = Path.Combine(t.Root, "log.txt");
        File.WriteAllLines(log + ".old", Enumerable.Range(1, 400).Select(i => $"old {i}"));
        File.WriteAllLines(log, Enumerable.Range(1, 100).Select(i => $"new {i}"));

        var tail = Diagnostics.LogTail(log, Diagnostics.LogLines);

        Assert.Equal(300, tail.Count);
        Assert.Equal("old 201", tail[0]);
        Assert.Equal("new 100", tail[^1]);
    }

    [Fact]
    public void Saving_writes_one_text_file_in_the_folder()
    {
        using var t = new TempInstall();
        var desktop = Path.Combine(t.Root, "Desktop");

        var path = Diagnostics.Save("hello", desktop, T0);

        Assert.Equal(desktop, Path.GetDirectoryName(path));
        Assert.EndsWith(".txt", path);
        Assert.Equal("hello", File.ReadAllText(path));
        Assert.Single(Directory.GetFiles(desktop));
    }
}
