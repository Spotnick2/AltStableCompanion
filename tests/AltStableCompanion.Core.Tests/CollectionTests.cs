using AltStableCompanion.Core;

namespace AltStableCompanion.Core.Tests;

public class CollectionTests
{
    private static readonly DateTime T0 = new(2026, 9, 29, 1, 6, 38);
    private static readonly DateTime Written = new(2026, 9, 26, 1, 50, 0);

    // Distinct, not square: a row's Size has to be the entry's W and H in that order.
    private static ManifestEntry ByGuid(string guid, string file) => new(guid, guid, file, 3, 7, 4, 8, null, null);

    private static ManifestEntry Legacy(string fileBase) => new(fileBase, null, fileBase + ".tga", 5, 9, 8, 16, null, null);

    private static CharacterStatus Captured(string name, string guid, CharacterState state = CharacterState.Portrait,
        string? note = null) => new(guid, name, T0, state, note);

    private static IReadOnlyList<PortraitRow> Build(ManifestEntry[] entries, params CharacterStatus[] captures) =>
        Collection.Build(entries, captures, entries.ToDictionary(e => e.FileName, _ => Written));

    [Fact]
    public void The_list_puts_the_one_being_worked_on_first_then_the_latest_activity()
    {
        var entries = new[] { ByGuid("Player-1-AAAA", "aaa.tga"), ByGuid("Player-1-BBBB", "bbb.tga"), ByGuid("Player-1-CCCC", "ccc.tga") with { Enhanced = new EnhancedTexture(1, 2, 4, 8) } };
        var times = new Dictionary<string, DateTime>
        {
            ["aaa.tga"] = Written.AddDays(2),                                  // the newest portrait
            ["bbb.tga"] = Written,
            ["ccc.tga"] = Written.AddDays(-5),
            [CutoutFolder.EnhancedKey("ccc.tga")] = Written.AddDays(3),        // but the newest thing of all is C's enhanced picture
        };
        var rows = Collection.Build(entries, [Captured("Aaa", "Player-1-AAAA"), Captured("Bbb", "Player-1-BBBB"), Captured("Ccc", "Player-1-CCCC")], times,
            guid => guid == "Player-1-BBBB" ? CutoutFolder.EnhancingNote : null);
        Assert.Equal(["Bbb", "Ccc", "Aaa"], rows.Select(r => r.Name));
        Assert.True(rows[0].Enhancing);
        Assert.Equal(Written.AddDays(3), rows[1].EnhancedModified);
        Assert.Equal(Written.AddDays(3), rows[1].LastActivity);
        // Without any activity, by name.
        var quiet = Collection.Build(entries, [], new Dictionary<string, DateTime>());
        Assert.Equal(["Aaa", "Bbb", "Ccc"], quiet.Select(r => r.Name));
    }

    [Fact]
    public void A_character_s_portrait_is_the_one_keyed_by_its_guid()
    {
        var row = Build([ByGuid("Player-1-AAAA", "kaleid-sumner.tga")], Captured("Kaleid Sumner", "Player-1-AAAA")).Single();
        Assert.Equal(("Kaleid Sumner", "Player-1-AAAA", "kaleid-sumner.tga", PortraitSource.ByGuid),
            (row.Name, row.Guid, row.FileName, row.Source));
        Assert.Equal(CaptureOutcome.Converted, row.Outcome);
        Assert.Equal((Written, T0), (row.FileModified, row.LatestCapture));
        Assert.True(row.Ready);
        Assert.False(row.NeedsAttention);
        // The manifest's crop, W then H: what a thumbnail shows.
        Assert.Equal((3, 7), row.Size);
    }

    [Fact]
    public void Without_one_it_is_the_portrait_that_has_its_name()
    {
        // A portrait from before portraits carried a guid: the game shows it on whoever has
        // that name, and so does the list. One row, not two.
        var row = Build([Legacy("kaleid-sumner")], Captured("Kaleid Sumner", "Player-1-AAAA", CharacterState.Missing, "no screenshots")).Single();
        Assert.Equal(PortraitSource.ByName, row.Source);
        Assert.Equal("kaleid-sumner.tga", row.FileName);
        Assert.Equal((5, 9), row.Size);
        Assert.Equal(CaptureOutcome.NoScreenshots, row.Outcome);
        // The portrait is there. That capture's screenshots are gone for good: nothing to do.
        Assert.False(row.NeedsAttention);
    }

    [Fact]
    public void A_portrait_that_names_another_guid_is_nobody_else_s()
    {
        // Keyed by a slug AND naming a guid: the game refuses it for any other character.
        var named = new ManifestEntry("twin", "Player-1-AAAA", "twin.tga", 1, 1, 1, 1, null, null);
        var rows = Build([named], Captured("Twin", "Player-2-BBBB", CharacterState.Missing));
        Assert.Equal(2, rows.Count);
        Assert.Equal(PortraitSource.None, rows.Single(r => r.Guid == "Player-2-BBBB").Source);
        Assert.Equal(PortraitSource.File, rows.Single(r => r.Guid == "Player-1-AAAA").Source);
    }

    [Fact]
    public void A_guid_portrait_wins_over_a_file_with_the_same_name()
    {
        // Both exist. The character gets its own; the other is a file, listed as a file.
        var rows = Build([ByGuid("Player-1-AAAA", "aaa-bbb-1aaaa.tga"), Legacy("aaa-bbb")], Captured("Aaa Bbb", "Player-1-AAAA"));
        Assert.Equal(2, rows.Count);
        var mine = rows.Single(r => r.Guid is not null);
        Assert.Equal(("aaa-bbb-1aaaa.tga", PortraitSource.ByGuid), (mine.FileName, mine.Source));
        var file = rows.Single(r => r.Guid is null);
        Assert.Equal(("Aaa Bbb", "aaa-bbb.tga", PortraitSource.File, CaptureOutcome.None), (file.Name, file.FileName, file.Source, file.Outcome));
    }

    [Fact]
    public void Two_characters_whose_names_slug_alike_both_find_the_one_file()
    {
        // "O'Brien" and "O Brien" are two characters and one slug. The file is shown on both
        // in game; the list says so on both, and lists the file no third time.
        var rows = Build([Legacy("o-brien")],
            Captured("O'Brien", "Player-1-AAAA", CharacterState.Missing), Captured("O Brien", "Player-2-BBBB", CharacterState.Missing));
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Equal((PortraitSource.ByName, "o-brien.tga"), (r.Source, r.FileName)));
    }

    [Fact]
    public void Namesakes_are_told_apart_by_their_guid()
    {
        var rows = Build([ByGuid("Player-1-AAAA", "twin.tga"), ByGuid("Player-2-BBBB", "twin-2bbbb.tga"), ByGuid("Player-3-CCCC", "solo.tga")],
            Captured("Twin", "Player-2-BBBB"), Captured("Twin", "Player-1-AAAA"), Captured("Solo", "Player-3-CCCC"));
        Assert.Equal(["Player-3-CCCC", "Player-1-AAAA", "Player-2-BBBB"], rows.Select(r => r.Guid));
        Assert.Equal([false, true, true], rows.Select(r => r.ShowGuid));
    }

    [Fact]
    public void A_file_nobody_captured_is_listed_by_what_its_name_reads_like()
    {
        var rows = Build([Legacy("zoruka-mortalis"), Legacy("karuzo-elegia")]);
        Assert.Equal(["Karuzo Elegia", "Zoruka Mortalis"], rows.Select(r => r.Name));
        Assert.All(rows, r =>
        {
            Assert.Equal(PortraitSource.File, r.Source);
            Assert.Null(r.LatestCapture);
            Assert.Equal(Written, r.FileModified);
            Assert.Equal((5, 9), r.Size);
            Assert.False(r.NeedsAttention);
        });
        Assert.Equal("Player 4395 0a1b2c3d", Collection.Label("player-4395-0a1b2c3d"));
        Assert.Equal("", Collection.Label(""));
    }

    [Fact]
    public void A_capture_with_no_portrait_is_a_row_that_needs_the_player()
    {
        var rows = Build([],
            Captured("Aaa", "g1", CharacterState.Rejected, "identical shots"),
            Captured("Bbb", "g2", CharacterState.Missing, "no screenshots"),
            Captured("Ccc", "g3", CharacterState.Failed, "access denied"),
            Captured("Ddd", "g4", CharacterState.Collided, "same second"),
            Captured("Eee", "g5", CharacterState.Ambiguous, "left alone"));
        Assert.Equal(
            [CaptureOutcome.Unusable, CaptureOutcome.NoScreenshots, CaptureOutcome.Failed, CaptureOutcome.Unusable, CaptureOutcome.Unusable],
            rows.Select(r => r.Outcome));
        Assert.All(rows, r =>
        {
            Assert.False(r.Ready);
            Assert.True(r.NeedsAttention);
            Assert.Null(r.FileModified);
            Assert.Null(r.Size);
        });
        Assert.Equal("identical shots", rows[0].Note);
    }

    [Fact]
    public void A_screenshot_still_being_written_is_not_one_that_is_gone()
    {
        var rows = Build([],
            Captured("Aaa", "g1", CharacterState.Missing, "a screenshot is in use - next pass") with { Transient = true },
            Captured("Bbb", "g2", CharacterState.Missing, "no screenshots for this capture on disk"));
        Assert.Equal([CaptureOutcome.Writing, CaptureOutcome.NoScreenshots], rows.Select(r => r.Outcome));
        Assert.Equal([false, true], rows.Select(r => r.NeedsAttention));
    }

    [Fact]
    public void An_older_portrait_with_a_newer_capture_that_failed_is_ready_and_needs_the_player()
    {
        var row = Build([ByGuid("g1", "aaa.tga")], Captured("Aaa", "g1", CharacterState.Rejected, "identical shots")).Single();
        Assert.True(row.Ready);
        Assert.True(row.NeedsAttention);
        Assert.Equal((Written, T0), (row.FileModified, row.LatestCapture));
    }

    [Fact]
    public void What_is_not_known_or_looks_wrong_is_carried_onto_the_row()
    {
        var undated = Build([ByGuid("g1", "aaa.tga")], Captured("Aaa", "g1") with { Undated = true }).Single();
        Assert.Equal(CaptureOutcome.Unknown, undated.Outcome);
        Assert.False(undated.NeedsAttention);

        var square = Build([ByGuid("g1", "aaa.tga")], Captured("Aaa", "g1") with { NearlySquare = true }).Single();
        Assert.True(square.NearlySquare);
        Assert.True(square.NeedsAttention);

        var both = Build([ByGuid("g1", "aaa.tga")], Captured("Aaa", "g1") with { Undated = true, NearlySquare = true }).Single();
        Assert.Equal(CaptureOutcome.Unknown, both.Outcome);
        Assert.True(both.NeedsAttention);
    }
}
