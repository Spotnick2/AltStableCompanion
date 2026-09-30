using AltStableCompanion.Core;

namespace AltStableCompanion.Core.Tests;

public class EnhancedManifestTests
{
    private const string Guid1 = "Player-1-0000AAAA";

    // A primary with a sidecar, and an enhanced picture of it made "from" that primary:
    // hashes taken from the files as written, so the rule sees a match unless a test breaks it.
    private static (CutoutFolder Folder, string Primary, string Enhanced) Pair(TempInstall t, string? guid = Guid1, long? epoch = 5)
    {
        var f = new CutoutFolder(t.Install.CutoutAddonDir);
        f.EnsureToc();
        f.WriteCutout("kaleid-sumner", TestData.Solid(4, 4, 1, 2, 3), new CutoutMeta { W = 3, H = 4, TexW = 4, TexH = 4, Guid = guid, Epoch = epoch });
        var primary = Path.Combine(f.CutoutsDir, "kaleid-sumner.tga");
        Directory.CreateDirectory(f.EnhancedDir);
        var enhanced = Path.Combine(f.EnhancedDir, "kaleid-sumner.tga");
        TgaCodec.Write(enhanced, TestData.Solid(8, 8, 9, 9, 9));
        WriteEnhancedSidecar(f, "kaleid-sumner", new CutoutMeta
        {
            W = 6, H = 8, TexW = 8, TexH = 8, Guid = guid, Epoch = epoch,
            Enhancement = new EnhancementMeta
            {
                SourceHash = EnhancementSignature.HashOf(primary), OutputHash = EnhancementSignature.HashOf(enhanced),
                Style = "wow-like", Model = "gpt-6-astra", Effort = "low", Prompt = 1, Signature = "sig",
                Generated = new DateTime(2026, 9, 30, 1, 0, 0, DateTimeKind.Utc),
            },
        });
        return (f, primary, enhanced);
    }

    // The enhanced sidecar has the primary sidecar's shape: written through the same serializer.
    private static void WriteEnhancedSidecar(CutoutFolder f, string fileBase, CutoutMeta meta)
    {
        var scratch = new CutoutFolder(Path.Combine(Path.GetTempPath(), "asc-sidecar-" + System.Guid.NewGuid().ToString("N")));
        scratch.WriteCutout(fileBase, TestData.Solid(1, 1, 0, 0, 0), meta);
        File.Copy(Path.Combine(scratch.CutoutsDir, fileBase + ".json"), Path.Combine(f.EnhancedDir, fileBase + ".json"), overwrite: true);
        Directory.Delete(scratch.AddonDir, recursive: true);
    }

    [Fact]
    public void An_enhanced_picture_is_attached_when_every_condition_holds_and_written_as_a_whole_descriptor()
    {
        using var t = new TempInstall();
        var (f, _, _) = Pair(t);
        var entry = f.Inventory().Single();
        Assert.Equal(new EnhancedTexture("kaleid-sumner.tga", 6, 8, 8, 8), entry.Enhanced);
        Assert.Equal((3, 4), (entry.W, entry.H));
        var lua = ManifestWriter.Render([entry], new DateTime(2026, 9, 30));
        Assert.Contains(@"enhanced = { file = [[Interface\AddOns\AltStableCutouts\Cutouts\Enhanced\kaleid-sumner.tga]], w = 6, h = 8, texw = 8, texh = 8 }", lua);
        Assert.Contains(@"file = [[Interface\AddOns\AltStableCutouts\Cutouts\kaleid-sumner.tga]], w = 3, h = 4", lua);
        // The enhanced picture is never listed as a portrait of its own.
        Assert.Single(f.Inventory());
    }

    [Fact]
    public void Each_condition_of_the_rule_holds_the_picture_back_on_its_own()
    {
        using var t = new TempInstall();
        var (f, primary, enhanced) = Pair(t);
        EnhancedTexture? Now() => f.Inventory().Single().Enhanced;
        Assert.NotNull(Now());

        // The primary changed underneath: its bytes are not the ones the picture was made from.
        var bytes = File.ReadAllBytes(primary);
        TgaCodec.Write(primary, TestData.Solid(4, 4, 7, 7, 7));
        Assert.Null(Now());
        File.WriteAllBytes(primary, bytes);
        Assert.NotNull(Now());

        // The enhanced file is not the one made.
        var made = File.ReadAllBytes(enhanced);
        TgaCodec.Write(enhanced, TestData.Solid(8, 8, 1, 1, 1));
        Assert.Null(Now());
        File.WriteAllBytes(enhanced, made);
        Assert.NotNull(Now());

        // The enhanced file is gone; the sidecar alone attaches nothing.
        File.Move(enhanced, enhanced + ".away");
        Assert.Null(Now());
        File.Move(enhanced + ".away", enhanced);
        Assert.NotNull(Now());

        // In use: not this time, and no error.
        using (new FileStream(enhanced, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.Null(Now());
        }
        Assert.NotNull(Now());
    }

    [Fact]
    public void Another_guid_a_missing_guid_and_bad_sizes_never_attach()
    {
        using var t = new TempInstall();
        var (f, primary, enhanced) = Pair(t);
        var meta = f.ReadEnhancedMeta("kaleid-sumner")!;

        WriteEnhancedSidecar(f, "kaleid-sumner", meta with { Guid = "Player-1-0000BBBB" });
        Assert.Null(f.Inventory().Single().Enhanced);
        WriteEnhancedSidecar(f, "kaleid-sumner", meta with { W = 0 });
        Assert.Null(f.Inventory().Single().Enhanced);
        WriteEnhancedSidecar(f, "kaleid-sumner", meta with { TexH = -8 });
        Assert.Null(f.Inventory().Single().Enhanced);
        // Hashes at the top level, not inside the enhancement block: not the contract's shape.
        WriteEnhancedSidecar(f, "kaleid-sumner", meta with { Enhancement = null });
        Assert.Null(f.Inventory().Single().Enhanced);
        WriteEnhancedSidecar(f, "kaleid-sumner", meta);
        Assert.NotNull(f.Inventory().Single().Enhanced);

        // A name-only legacy primary (no guid of its own) never attaches, whatever the sidecar says.
        using var t2 = new TempInstall();
        var (f2, _, _) = Pair(t2, guid: null, epoch: null);
        Assert.Null(f2.Inventory().Single().Enhanced);
        Assert.Null(f2.Inventory().Single().Guid);

        // An undated primary (the Python script's) attaches by hash alone.
        using var t3 = new TempInstall();
        var (f3, _, _) = Pair(t3, epoch: null);
        Assert.NotNull(f3.Inventory().Single().Enhanced);
    }

    [Fact]
    public void The_manifest_changes_when_the_attachment_does_and_a_rebuild_without_it_is_restored_by_the_next()
    {
        using var t = new TempInstall();
        var (f, _, enhanced) = Pair(t);
        Assert.True(f.WriteManifest(f.Inventory(), new DateTime(2026, 9, 30)));
        Assert.Contains("enhanced = {", File.ReadAllText(f.ManifestPath));
        // An old writer's manifest: the same entries without the field.
        var plain = f.Inventory().Select(e => e with { Enhanced = null }).ToList();
        Assert.True(f.WriteManifest(plain, new DateTime(2026, 9, 30, 0, 1, 0)));
        Assert.DoesNotContain("enhanced = {", File.ReadAllText(f.ManifestPath));
        // The files stayed; the next enhancement-aware rebuild puts the field back.
        Assert.True(File.Exists(enhanced));
        Assert.True(f.WriteManifest(f.Inventory(), new DateTime(2026, 9, 30, 0, 2, 0)));
        Assert.Contains("enhanced = {", File.ReadAllText(f.ManifestPath));
        // And nothing changed: nothing written.
        Assert.False(f.WriteManifest(f.Inventory(), new DateTime(2026, 9, 30, 0, 3, 0)));
    }

    [Fact]
    public void The_roster_marker_is_installed_support_and_nothing_less()
    {
        using var t = new TempInstall();
        Assert.False(t.Install.RosterDrawsEnhanced);
        var toc = Path.Combine(t.Install.AddOnsDir, "AltStableRoster", "AltStableRoster.toc");
        Directory.CreateDirectory(Path.GetDirectoryName(toc)!);
        File.WriteAllText(toc, "## Title: AltStable Roster\n## LoadOnDemand: 1\n");
        Assert.False(t.Install.RosterDrawsEnhanced);
        File.WriteAllText(toc, "## Title: AltStable Roster\n## X-AltStable-Enhanced: 1\n");
        Assert.True(t.Install.RosterDrawsEnhanced);
        File.WriteAllText(toc, "## X-AltStable-Enhanced: 0\n");
        Assert.False(t.Install.RosterDrawsEnhanced);
        File.WriteAllText(toc, "## X-AltStable-Enhanced: yes\n");
        Assert.False(t.Install.RosterDrawsEnhanced);
    }

    [Fact]
    public void The_enhancement_settings_are_off_by_default_bounded_and_kept()
    {
        using var t = new TempInstall();
        var dir = Path.Combine(t.Root, "data");
        var d = new Settings();
        Assert.False(d.Enhance);
        Assert.Equal((10, "wow-like", "gpt-6-astra", "low", 360), (d.EnhanceMinLevel, d.EnhanceStyle, d.EnhanceModel, d.EnhanceEffort, d.EnhanceTimeoutSeconds));
        var s = new Settings { Enhance = true, EnhanceMinLevel = 0, EnhanceStyle = "REALISTIC", EnhanceModel = " ", EnhanceEffort = "Ultra", EnhanceTimeoutSeconds = 5 };
        Assert.Equal((1, "realistic", "gpt-6-astra", "low", 60), (s.EnhanceMinLevel, s.EnhanceStyle, s.EnhanceModel, s.EnhanceEffort, s.EnhanceTimeoutSeconds));
        s = s with { EnhanceMinLevel = 99, EnhanceEffort = "High", EnhanceModel = "other-model", EnhanceTimeoutSeconds = 5000 };
        Assert.Equal((60, "high", "other-model", 1800), (s.EnhanceMinLevel, s.EnhanceEffort, s.EnhanceModel, s.EnhanceTimeoutSeconds));
        s.Save(dir);
        Assert.Equal(s, Settings.Load(dir));
        Assert.NotEqual(s, s with { Enhance = false });
        Assert.NotEqual(s, s with { EnhanceStyle = "cartoonish" });
    }

    [Fact]
    public void A_record_without_class_race_or_gender_is_not_one_to_spend_on()
    {
        RosterCharacter Char(string guid, string? cls = "MAGE", string? race = "Human", string? gender = "Male") =>
            new(guid, "Name", cls, race, gender, 20, 1);
        var roster = new RosterStore("a", [Char("g1"), Char("g2", cls: null), Char("g3", race: ""), Char("g4", gender: " ")], new HashSet<string>());
        var picked = Eligibility.Select(new SavedVariablesSnapshot([], [roster], []), 10, _ => "f");
        Assert.Equal(["g1"], picked.Candidates.Select(c => c.Guid));
    }
}
