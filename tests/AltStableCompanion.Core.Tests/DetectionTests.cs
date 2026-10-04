using AltStableCompanion.Core;

namespace AltStableCompanion.Core.Tests;

/// <summary>
/// Which game detection picks: the one AltStable is installed in. Measured on the owner's PC
/// (2026-10-04): seven flavour folders under one WoW folder, AltStable in _classic_beta_ only;
/// the registry's InstallPath names _anniversary_ (the last one run), its Beta key
/// _classic_beta_.
/// </summary>
public class DetectionTests
{
    // A flavour folder beside the TempInstall's _classic_beta_: an executable, and AltStable or not.
    private static string Flavor(TempInstall t, string name, bool altStable, string exe = "WowClassic.exe")
    {
        var dir = Path.Combine(t.Root, name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, exe), "");
        if (altStable) AltStable(dir);
        return dir;
    }

    private static void AltStable(string flavorDir)
    {
        var addon = Path.Combine(flavorDir, "Interface", "AddOns", "AltStable");
        Directory.CreateDirectory(addon);
        File.WriteAllText(Path.Combine(addon, "AltStable.toc"), "## Title: AltStable\n");
    }

    [Fact]
    public void One_flavour_with_AltStable_is_the_one_detected_even_when_it_is_no_candidate()
    {
        // The candidates are what the registry and the default name give: _classic_beta_,
        // which has no AltStable here. _anniversary_ has it.
        using var t = new TempInstall();
        var anniversary = Flavor(t, "_anniversary_", altStable: true);
        Flavor(t, "_retail_", altStable: false, exe: "Wow.exe");
        Directory.CreateDirectory(Path.Combine(t.Root, "_beta_"));        // no executable: not a flavour

        Assert.Equal(anniversary, WowInstallLocator.Detect([t.Install.FlavorDir])!.FlavorDir);
        Assert.Empty(WowInstallLocator.AltStableElsewhere(new WowInstall(anniversary)));
    }

    [Fact]
    public void With_two_the_candidates_order_decides_and_the_other_is_named()
    {
        using var t = new TempInstall();
        AltStable(t.Install.FlavorDir);
        var anniversary = Flavor(t, "_anniversary_", altStable: true);

        // _classic_beta_ is a candidate, _anniversary_ only under the root: the candidate wins,
        // although _anniversary_ comes first by name - and nothing is decided by time.
        File.SetLastWriteTimeUtc(Path.Combine(anniversary, "WowClassic.exe"), DateTime.UtcNow.AddDays(1));
        Assert.Equal(t.Install.FlavorDir, WowInstallLocator.Detect([t.Install.FlavorDir])!.FlavorDir);
        Assert.Equal([anniversary], WowInstallLocator.AltStableElsewhere(t.Install));
        // Both candidates: their order, not the names'.
        Assert.Equal(anniversary, WowInstallLocator.Detect([anniversary, t.Install.FlavorDir])!.FlavorDir);
        Assert.Equal([t.Install.FlavorDir, anniversary], WowInstallLocator.WithAltStable([t.Install.FlavorDir]));
    }

    [Fact]
    public void With_none_it_is_the_first_candidate_that_is_a_flavour_as_before()
    {
        using var t = new TempInstall();
        Flavor(t, "_anniversary_", altStable: false);
        var missing = Path.Combine(t.Root, "_nothere_");

        Assert.Equal(t.Install.FlavorDir, WowInstallLocator.Detect([missing, t.Install.FlavorDir])!.FlavorDir);
        Assert.Null(WowInstallLocator.Detect([missing]));
        Assert.Empty(WowInstallLocator.WithAltStable([t.Install.FlavorDir]));
    }

    [Fact]
    public void The_window_names_the_other_game_only_for_a_detected_install()
    {
        using var t = new TempInstall();
        AltStable(t.Install.FlavorDir);
        var anniversary = Flavor(t, "_anniversary_", altStable: true);
        var data = Path.Combine(t.Root, "appdata");
        new Settings { Started = true }.Save(data);

        using (var c = new Controller(new StartupOptions(DataDir: data), () => WowInstallLocator.Detect([t.Install.FlavorDir])))
        {
            c.Start();
            Assert.Equal(t.Install.FlavorDir, c.Current.Shell.Install!.FlavorDir);
            Assert.Equal([anniversary], c.Current.AltStableElsewhere);
            Assert.Equal("AltStable is also installed in _anniversary_. To use that game instead, Browse to it in Settings.",
                PassText.AltStableElsewhere(c.Current.AltStableElsewhere));

            // Chosen with Browse: the player's own decision, nothing to say.
            Assert.True(c.Browse(anniversary));
            Assert.Null(c.Current.AltStableElsewhere);
            // Detect again: said again, about the game not in use.
            c.DetectAgain();
            Assert.Equal([anniversary], c.Current.AltStableElsewhere);
        }

        // Pinned with --wow-dir: nothing to say either.
        using var pinned = new Controller(new StartupOptions(WowDir: t.Install.FlavorDir, DataDir: data),
            () => throw new InvalidOperationException("detection was used"));
        pinned.Start();
        Assert.Null(pinned.Current.AltStableElsewhere);
    }

    [Fact]
    public void A_game_whose_addon_folder_is_gone_for_a_moment_is_still_the_one_detected()
    {
        // A redeploy or a reinstall: AltStable's folder is gone, its saved data is not. An old
        // AltStable in another game must not take over - the next pass would convert there.
        using var t = new TempInstall();
        t.WriteStore("1#1", TestData.SavedVariables([]));
        Flavor(t, "_anniversary_", altStable: true);

        Assert.Equal(t.Install.FlavorDir, WowInstallLocator.Detect([t.Install.FlavorDir])!.FlavorDir);
        Assert.True(WowInstallLocator.PlaysAltStable(t.Install.FlavorDir));
    }

    [Fact]
    public void Browsing_to_the_WoW_folder_picks_the_game_detection_would()
    {
        using var t = new TempInstall();                                   // _classic_beta_, no AltStable
        var anniversary = Flavor(t, "_anniversary_", altStable: true);
        Assert.Equal(anniversary, ResolvedInstall.FromPicked(t.Root)!.FlavorDir);
        Assert.Equal(t.Install.FlavorDir, ResolvedInstall.FromPicked(t.Install.FlavorDir)!.FlavorDir);
    }

    [Fact]
    public void A_folder_beside_the_game_that_cannot_be_read_is_skipped_not_thrown()
    {
        // Measured stand-in for a locked or half-removed folder: a junction whose target is gone.
        // Enumerating it fails at the file system, as an unreadable folder does.
        using var t = new TempInstall();
        AltStable(t.Install.FlavorDir);
        var target = Path.Combine(t.Root, "gone");
        Directory.CreateDirectory(target);
        var junction = Path.Combine(t.Root, "_broken_");
        using (var mk = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{junction}\" \"{target}\"")
               { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true }))
        {
            mk!.WaitForExit();
        }
        Directory.Delete(target);
        Assert.Throws<DirectoryNotFoundException>(() => Directory.EnumerateFiles(junction, "Wow*.exe").Any());

        Assert.Equal(t.Install.FlavorDir, WowInstallLocator.Detect([t.Install.FlavorDir])!.FlavorDir);
        Assert.Empty(WowInstallLocator.AltStableElsewhere(t.Install));
        Assert.False(WowInstallLocator.IsFlavorDir(junction));
        Directory.Delete(junction);
    }

    [Fact]
    public void Two_or_more_others_are_listed_by_name()
    {
        Assert.Null(PassText.AltStableElsewhere(null));
        Assert.Null(PassText.AltStableElsewhere([]));
        Assert.Equal("AltStable is also installed in _anniversary_, _classic_ and _retail_. To use one of those instead, Browse to it in Settings.",
            PassText.AltStableElsewhere([@"C:\WoW\_anniversary_", @"C:\WoW\_classic_", @"C:\WoW\_retail_"]));
    }
}
