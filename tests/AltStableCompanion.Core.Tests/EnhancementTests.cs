using System.Buffers.Binary;
using System.IO.Compression;
using AltStableCompanion.Core;

namespace AltStableCompanion.Core.Tests;

public class PngCodecTests
{
    [Fact]
    public void Bad_compressed_data_is_the_pictures_fault_in_the_codecs_words()
    {
        // A valid signature, IHDR and CRCs; the IDAT is not zlib at all.
        var ms = new MemoryStream();
        ms.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
        var ihdr = new byte[13];
        ihdr[3] = 2; ihdr[7] = 2; ihdr[8] = 8; ihdr[9] = 6;
        PngCodec.Chunk(ms, "IHDR", ihdr);
        PngCodec.Chunk(ms, "IDAT", new byte[] { 0, 0, 0, 0 });
        PngCodec.Chunk(ms, "IEND", []);
        var ex = Assert.Throws<PngFormatException>(() => PngCodec.Read(ms.ToArray()));
        Assert.Contains("not valid zlib", ex.Message);
    }

    // A PNG built by hand, byte by byte, so the reader is tested against the format and not
    // against our own writer: filter per line, IDAT split as asked, any colour type.
    private static byte[] Build(int w, int h, int channels, byte[][] filteredLines, int idatPieces = 1,
        byte colourType = 6, byte depth = 8, byte interlace = 0, byte[]? trns = null, bool breakCrc = false,
        int? truncateTo = null, byte[]? extraChunk = null, string? extraChunkName = null, bool skipIhdr = false)
    {
        var raw = filteredLines.SelectMany(l => l).ToArray();
        var zipped = new MemoryStream();
        using (var z = new ZLibStream(zipped, CompressionLevel.Fastest, leaveOpen: true)) z.Write(raw);
        var data = zipped.ToArray();

        var o = new MemoryStream();
        o.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        if (!skipIhdr)
        {
            var ihdr = new byte[13];
            BinaryPrimitives.WriteUInt32BigEndian(ihdr, (uint)w);
            BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(4), (uint)h);
            ihdr[8] = depth;
            ihdr[9] = colourType;
            ihdr[12] = interlace;
            PngCodec.Chunk(o, "IHDR", ihdr);
        }
        if (trns is not null) PngCodec.Chunk(o, "tRNS", trns);
        if (extraChunk is not null) PngCodec.Chunk(o, extraChunkName!, extraChunk);
        var per = Math.Max(1, (data.Length + idatPieces - 1) / idatPieces);
        for (var i = 0; i < data.Length; i += per) PngCodec.Chunk(o, "IDAT", data.AsSpan(i, Math.Min(per, data.Length - i)));
        PngCodec.Chunk(o, "IEND", []);
        var bytes = o.ToArray();
        if (breakCrc) bytes[^1] ^= 0xFF;
        return truncateTo is { } n ? bytes[..n] : bytes;
    }

    private static byte[] Line(byte filter, params byte[] samples) => [filter, .. samples];

    [Fact]
    public void Every_filter_is_undone_as_the_specification_says()
    {
        // 2 x 2 RGBA. Line 1: filter Sub - each sample adds the one bpp back. Line 2: Up.
        var png = Build(2, 2, 4, [
            Line(1, 10, 20, 30, 255, 5, 5, 5, 0),          // (10,20,30,255) then (15,25,35,255)
            Line(2, 1, 1, 1, 0, 1, 1, 1, 0),               // +1 on each of the line above
        ]);
        var img = PngCodec.Read(png);
        Assert.Equal((2, 2), (img.Width, img.Height));
        Assert.Equal(((byte)10, (byte)20, (byte)30, (byte)255), img[0, 0]);
        Assert.Equal(((byte)15, (byte)25, (byte)35, (byte)255), img[1, 0]);
        Assert.Equal(((byte)11, (byte)21, (byte)31, (byte)255), img[0, 1]);
        Assert.Equal(((byte)16, (byte)26, (byte)36, (byte)255), img[1, 1]);

        // Average: floor((left + up) / 2); Paeth: the nearest of left, up, up-left.
        var avg = PngCodec.Read(Build(2, 2, 4, [
            Line(0, 100, 0, 0, 255, 50, 0, 0, 255),
            Line(3, 50, 0, 0, 0, 0, 0, 0, 0),              // (0,0): 50 + (0+100)/2 = 100; (1,0): 0 + (100+50)/2 = 75
        ]));
        Assert.Equal((byte)100, avg[0, 1].R);
        Assert.Equal((byte)75, avg[1, 1].R);
        Assert.Equal((byte)127, avg[0, 1].A);               // 0 + (0 + 255)/2
        var paeth = PngCodec.Read(Build(2, 2, 4, [
            Line(0, 10, 0, 0, 255, 40, 0, 0, 255),
            Line(4, 2, 0, 0, 0, 3, 0, 0, 0),               // (0,1): a=0,b=10,c=0 -> p=10 -> b -> 12; (1,1): a=12,b=40,c=10 -> p=42 -> b -> 43
        ]));
        Assert.Equal((byte)12, paeth[0, 1].R);
        Assert.Equal((byte)43, paeth[1, 1].R);
    }

    [Fact]
    public void RGB_is_opaque_unless_tRNS_names_a_colour_and_IDAT_may_be_split()
    {
        var rgb = Build(2, 1, 3, [Line(0, 1, 2, 3, 9, 9, 9)], idatPieces: 3, colourType: 2, trns: [0, 9, 0, 9, 0, 9]);
        var img = PngCodec.Read(rgb);
        Assert.Equal(((byte)1, (byte)2, (byte)3, (byte)255), img[0, 0]);
        Assert.Equal(((byte)9, (byte)9, (byte)9, (byte)0), img[1, 0]);
        Assert.Equal((byte)255, PngCodec.Read(Build(1, 1, 3, [Line(0, 9, 9, 9)], colourType: 2))[0, 0].A);
    }

    [Fact]
    public void What_is_not_this_subset_is_refused_by_name_before_anything_large_is_allocated()
    {
        var good = new byte[][] { Line(0, 1, 2, 3, 4) };
        Assert.Contains("CRC", Assert.Throws<PngFormatException>(() => PngCodec.Read(Build(1, 1, 4, good, breakCrc: true))).Message);
        Assert.Contains("interlaced", Assert.Throws<PngFormatException>(() => PngCodec.Read(Build(1, 1, 4, good, interlace: 1))).Message);
        Assert.Contains("16-bit", Assert.Throws<PngFormatException>(() => PngCodec.Read(Build(1, 1, 4, good, depth: 16))).Message);
        Assert.Contains("palette", Assert.Throws<PngFormatException>(() => PngCodec.Read(Build(1, 1, 4, good, colourType: 3))).Message);
        Assert.Contains("greyscale", Assert.Throws<PngFormatException>(() => PngCodec.Read(Build(1, 1, 4, good, colourType: 0))).Message);
        Assert.Contains("critical", Assert.Throws<PngFormatException>(() => PngCodec.Read(Build(1, 1, 4, good, extraChunk: [1], extraChunkName: "ZZZZ"))).Message);
        // An ancillary chunk (lower-case first letter) is skipped.
        Assert.Equal((byte)1, PngCodec.Read(Build(1, 1, 4, good, extraChunk: [1], extraChunkName: "zTXt"))[0, 0].R);
        Assert.Contains("ends", Assert.Throws<PngFormatException>(() => PngCodec.Read(Build(1, 1, 4, good, truncateTo: 40))).Message);
        Assert.Contains("IHDR", Assert.Throws<PngFormatException>(() => PngCodec.Read(Build(1, 1, 4, good, skipIhdr: true))).Message);
        Assert.Contains("signature", Assert.Throws<PngFormatException>(() => PngCodec.Read(new byte[] { 1, 2, 3 })).Message);
        // The data does not match the header: too short, and too long.
        Assert.Contains("early", Assert.Throws<PngFormatException>(() => PngCodec.Read(Build(2, 1, 4, good))).Message);
        Assert.Contains("longer", Assert.Throws<PngFormatException>(() => PngCodec.Read(Build(1, 1, 4, [Line(0, 1, 2, 3, 4), Line(0, 5, 6, 7, 8)]))).Message);
        // Sizes a generated picture never has - refused from the header, before inflating.
        Assert.Contains("size", Assert.Throws<PngFormatException>(() => PngCodec.Read(Build(PngCodec.MaxSide + 1, 1, 4, good))).Message);
        Assert.Contains("size", Assert.Throws<PngFormatException>(() => PngCodec.Read(Build(4096, 4096, 4, good))).Message);
        Assert.Contains("filter type", Assert.Throws<PngFormatException>(() => PngCodec.Read(Build(1, 1, 4, [Line(7, 1, 2, 3, 4)]))).Message);
        // A chunk that claims to be almost 2 GB long: past the end, in any arithmetic.
        var huge = Build(1, 1, 4, good);
        BinaryPrimitives.WriteUInt32BigEndian(huge.AsSpan(8), 0x7FFFFFF0);
        Assert.Contains("past the end", Assert.Throws<PngFormatException>(() => PngCodec.Read(huge)).Message);
    }

    [Fact]
    public void A_file_larger_than_a_generated_picture_is_refused_before_it_is_read()
    {
        using var t = new TempInstall();
        var big = Path.Combine(t.Root, "big.png");
        using (var f = new FileStream(big, FileMode.Create))
        {
            f.SetLength(PngCodec.MaxEncodedBytes + 1);
        }
        var before = GC.GetTotalAllocatedBytes(precise: true);
        Assert.Contains("larger than", Assert.Throws<PngFormatException>(() => PngCodec.Read(big)).Message);
        // Not the file's worth of bytes: the refusal came from its length.
        Assert.True(GC.GetTotalAllocatedBytes(precise: true) - before < PngCodec.MaxEncodedBytes / 4);
        var small = Path.Combine(t.Root, "small.png");
        File.WriteAllBytes(small, PngCodec.Write(new RgbaImage(2, 2)));
        Assert.Equal(2, PngCodec.Read(small).Width);
    }

    [Fact]
    public void What_we_write_we_read_back_exactly()
    {
        var img = new RgbaImage(5, 3);
        for (var y = 0; y < 3; y++)
            for (var x = 0; x < 5; x++)
                img[x, y] = ((byte)(x * 40), (byte)(y * 90), (byte)(x + y), (byte)(x == 2 ? 0 : 254));
        var back = PngCodec.Read(PngCodec.Write(img));
        Assert.Equal(img.Pixels, back.Pixels);
        Assert.Equal((5, 3), (back.Width, back.Height));
    }
}

public class EligibilityTests
{
    private static RosterCharacter Char(string guid, string name, int level, long updated = 1, string? race = "Troll", string? cls = "WARLOCK", string? gender = "Female") =>
        new(guid, name, cls, race, gender, level, updated);

    private static RosterStore Store(string path, IReadOnlyList<RosterCharacter> roster, params string[] hidden) =>
        new(path, roster, new HashSet<string>(hidden));

    private static SavedVariablesSnapshot Snap(params RosterStore[] rosters) => new([], rosters, []);

    [Fact]
    public void The_roster_tables_are_read_from_the_same_file_as_the_captures_and_never_refuse_it()
    {
        // The shared SavedVariables() helper writes a decoy AltStableDB of its own, so this
        // file is written whole: the roster tables first, as the client does, then the store.
        var text = "AltStableDB = {\n[\"Player-1-AAAA\"] = {\n[\"name\"] = \"Kaleid Sumner\",\n[\"class\"] = \"WARLOCK\",\n[\"race\"] = \"Troll\",\n[\"gender\"] = \"Female\",\n[\"level\"] = 12,\n[\"lastUpdate\"] = 1790000000,\n[\"gearname_head\"] = \"\",\n},\n[\"not a guid!\"] = {\n[\"name\"] = \"Nope\",\n},\n[\"Player-1-BBBB\"] = {\n[\"level\"] = 3,\n},\n}\n" +
            "AltStableConfig = {\n[\"hiddenCharacters\"] = {\n[\"Player-1-CCCC\"] = true,\n[\"Player-1-DDDD\"] = false,\n},\n[\"favouriteCharacters\"] = {\n},\n}\n" +
            "AltStablePortraits = {\n[\"version\"] = 1,\n[\"renders\"] = {\n" + TestData.Record("Kaleid Sumner", "Player-1-AAAA", 1, new DateTime(2026, 9, 29, 12, 0, 0)) + ",\n},\n}\n";
        var store = SavedVariablesReader.Parse(text, "x")!;
        Assert.Single(store.Renders);
        var roster = SavedVariablesReader.ParseRoster(text, "x");
        var c = Assert.Single(roster.Characters);
        Assert.Equal(("Player-1-AAAA", "Kaleid Sumner", "WARLOCK", "Troll", "Female", 12, 1790000000L),
            (c.Guid, c.Name, c.Class, c.Race, c.Gender, c.Level, c.Updated));
        Assert.Equal(["Player-1-CCCC"], roster.Hidden);

        // The shared helper's file has a decoy roster: its one character, with nothing known.
        var decoy = SavedVariablesReader.ParseRoster(TestData.SavedVariables([TestData.Record("A", "Player-1-AAAA", 1, new DateTime(2026, 9, 29))]), "x");
        Assert.Equal(("Player-1-0001", "Some Alt", 0), (decoy.Characters.Single().Guid, decoy.Characters.Single().Name, decoy.Characters.Single().Level));
        Assert.Empty(decoy.Hidden);
        // No roster tables at all, or one that stops mid-table: captures are still read, the roster is empty.
        var store2 = "AltStablePortraits = {\n[\"version\"] = 1,\n[\"renders\"] = {\n" + TestData.Record("A", "Player-1-AAAA", 1, new DateTime(2026, 9, 29)) + ",\n},\n}\n";
        Assert.Single(SavedVariablesReader.Parse(store2, "x")!.Renders);
        Assert.Empty(SavedVariablesReader.ParseRoster(store2, "x").Characters);
        Assert.Empty(SavedVariablesReader.ParseRoster(store2, "x").Hidden);
        var brokenText = "AltStableDB = {\n[\"Player-1-AAAA\"] = {\n" + store2;
        Assert.Single(SavedVariablesReader.Parse(brokenText, "x")!.Renders);
        var brokenRoster = SavedVariablesReader.ParseRoster(brokenText, "x");
        Assert.Empty(brokenRoster.Characters);
        Assert.Contains("AltStableDB", brokenRoster.Problem);
        // A hidden table that stops mid-way is not "nobody hidden": it is not known.
        var brokenHidden = SavedVariablesReader.ParseRoster("AltStableConfig = {\n[\"hiddenCharacters\"] = {\n[\"Player-1-AAAA\"] = true,\n" + store2, "x");
        Assert.Contains("AltStableConfig", brokenHidden.Problem);
        Assert.Null(SavedVariablesReader.ParseRoster(store2, "x").Problem);
    }

    [Fact]
    public void Every_account_hides_whatever_its_capture_store_says_and_a_file_not_read_is_known()
    {
        using var t = new TempInstall();
        // Account A captured; account B never did (nil, what the client writes) but hides a
        // character; account C's store is from a version this app does not know.
        t.WriteStore("A#1", TestData.SavedVariables([TestData.Record("Aaa", "Player-1-AAAA", 1, new DateTime(2026, 9, 29))]));
        t.WriteStore("B#1", "AltStableConfig = {\n[\"hiddenCharacters\"] = {\n[\"Player-1-AAAA\"] = true,\n},\n}\nAltStablePortraits = nil\n");
        t.WriteStore("C#1", "AltStableDB = {\n[\"Player-1-CCCC\"] = {\n[\"name\"] = \"Ccc\",\n[\"level\"] = 50,\n},\n}\nAltStablePortraits = {\n[\"version\"] = 99,\n}\n");
        var snap = SavedVariablesReader.Snapshot(t.Install.AccountsDir);
        Assert.Equal(2, snap.Stores.Count);                   // B is nil: not a store
        Assert.Equal(3, snap.Rosters.Count);                  // but every account has a roster
        Assert.Contains("Player-1-AAAA", snap.Rosters.Single(r => r.Path.Contains("B#1")).Hidden);
        Assert.Equal("Ccc", snap.Rosters.Single(r => r.Path.Contains("C#1")).Characters.Single().Name);
        Assert.Empty(snap.Skipped);
        // The old entry point is the same read.
        Assert.Equal(2, SavedVariablesReader.ReadAll(t.Install.AccountsDir).Count);

        // A file that cannot be read is named, and nobody is eligible from that snapshot.
        var locked = Path.Combine(t.Install.AccountsDir, "A#1", "SavedVariables", "AltStable.lua");
        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var partial = SavedVariablesReader.Snapshot(t.Install.AccountsDir);
            Assert.Equal([locked], partial.Skipped);
            Assert.Equal(2, partial.Rosters.Count);
            var refused = Eligibility.Select(partial, 1, _ => "f");
            Assert.Empty(refused.Candidates);
            Assert.Contains("not permission", refused.Refused);
        }

        // A roster table cut short in one account, with the character intact in another:
        // still nobody, and the message names the account.
        t.WriteStore("D#1", "AltStableConfig = {\n[\"hiddenCharacters\"] = {\n[\"Player-1-AAAA\"] = true,\n");
        var torn = SavedVariablesReader.Snapshot(t.Install.AccountsDir);
        Assert.Empty(torn.Skipped);
        var unknown = Eligibility.Select(torn, 1, _ => "f");
        Assert.Empty(unknown.Candidates);
        Assert.Contains("D#1", unknown.Refused);
        Assert.Contains("not permission", unknown.Refused);
    }

    [Fact]
    public void Eligible_is_of_the_level_not_hidden_anywhere_and_owning_a_portrait_by_guid()
    {
        var a = Store("a", [Char("g1", "Aaa", 12), Char("g2", "Bbb", 9), Char("g3", "Ccc", 30), Char("g4", "Ddd", 40), Char("g6", "Fff", 10)], "g3");
        var b = Store("b", [Char("g3", "Ccc", 31)], "g5");
        IReadOnlyList<EnhanceCandidate> picked = Eligibility.Select(Snap(a, b), 10, guid => guid == "g4" ? null : guid.ToLowerInvariant() + "-file").Candidates;
        // g2 is level 9; g3 is hidden in account a although visible in b; g4 has no portrait of
        // its own; g6 is exactly the minimum, which is enough.
        Assert.Equal(["g1", "g6"], picked.Select(p => p.Guid));
        Assert.Equal("g1-file", picked[0].FileBase);
        Assert.Equal(["g1", "g2", "g6"], Eligibility.Select(Snap(a, b), 1, guid => guid == "g4" ? null : guid + "-f").Candidates.Select(p => p.Guid));
    }

    [Fact]
    public void Two_accounts_records_of_one_character_settle_one_way()
    {
        // The highest level seen; the rest from the most recently updated record.
        var a = Store("a", [Char("g1", "Old Name", 20, updated: 100, race: "Troll")]);
        var b = Store("b", [Char("g1", "New Name", 15, updated: 200, race: "Orc")]);
        var one = Assert.Single(Eligibility.Select(Snap(a, b), 10, _ => "f").Candidates);
        Assert.Equal(("New Name", "Orc", 20), (one.Character.Name, one.Character.Race, one.Character.Level));
        // The other order gives the same answer.
        var same = Assert.Single(Eligibility.Select(Snap(b, a), 10, _ => "f").Candidates);
        Assert.Equal(one, same);
        // The same moment (both 0 when lastUpdate is missing): the file that sorts first, whichever came first.
        var x = Store("x", [Char("g1", "From X", 20, updated: 0, race: "Troll")]);
        var y = Store("y", [Char("g1", "From Y", 20, updated: 0, race: "Orc")]);
        Assert.Equal("From X", Assert.Single(Eligibility.Select(Snap(x, y), 10, _ => "f").Candidates).Character.Name);
        Assert.Equal("From X", Assert.Single(Eligibility.Select(Snap(y, x), 10, _ => "f").Candidates).Character.Name);
    }
}

public class EnhancementPromptTests
{
    private static readonly RosterCharacter Troll = new("g", "Drakuzo", "WARLOCK", "Troll", "Female", 12, 1);

    [Fact]
    public void The_reference_rules_and_the_prompt_says_only_what_it_cannot()
    {
        var p = EnhancementPrompt.Build(Troll, EnhanceStyles.WowLike);
        Assert.Contains("a Female Troll Warlock", p);
        Assert.Contains("It is the authority", p);
        Assert.Contains("two toes on each foot, three fingers on each hand", p);
        Assert.Contains("Troll anatomy", p);
        Assert.Contains("TRANSPARENT background", p);
        Assert.Contains("EXACTLY ONE image", p);
        Assert.Contains("ARTIFACT_PATH: ", p);
        Assert.Contains("do not add weapons, magic effects, glows, pets", p);
        // No gear words, no scenery, no crop talk: the reference carries the clothing.
        Assert.DoesNotContain("gear", p, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("crop", p, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Background:", p);
    }

    [Fact]
    public void Each_style_and_race_has_its_line_and_an_unknown_race_has_none()
    {
        Assert.Contains("photorealistic", EnhancementPrompt.Build(Troll, EnhanceStyles.Realistic));
        Assert.Contains("cartoon", EnhancementPrompt.Build(Troll, EnhanceStyles.Cartoonish));
        Assert.Contains("World of Warcraft style", EnhancementPrompt.Build(Troll, "no such style"));
        Assert.Contains("hooves", EnhancementPrompt.Anatomy("Tauren")!);
        Assert.Contains("green skin", EnhancementPrompt.Anatomy("orc")!);
        Assert.Contains("greyish undead", EnhancementPrompt.Anatomy("Scourge")!);
        Assert.Null(EnhancementPrompt.Anatomy("Pandaren"));
        var noRace = EnhancementPrompt.Build(Troll with { Race = null, Gender = null, Class = null }, EnhanceStyles.WowLike);
        Assert.Contains("cutout of a character from", noRace);
        Assert.DoesNotContain("anatomy", noRace);
        // The token is spelled as a reader would say it, and the client's display name wins.
        var elf = EnhancementPrompt.Build(Troll with { Race = "NightElf", RaceName = null }, EnhanceStyles.WowLike);
        Assert.Contains("a Female Night Elf Warlock", elf);
        Assert.Contains("Night Elf anatomy", elf);
        Assert.DoesNotContain("NightElf", elf);
        var undead = EnhancementPrompt.Build(Troll with { Race = "Scourge", RaceName = "Undead" }, EnhanceStyles.WowLike);
        Assert.Contains("a Female Undead Warlock", undead);
        Assert.Contains("Undead anatomy", undead);
        Assert.DoesNotContain("Scourge", undead);
        // Forever's own race: the model has never seen one, so the picture is the whole truth.
        var sky = EnhancementPrompt.Build(Troll with { Race = "Skyborne", RaceName = "Windshaper Skyborne" }, EnhanceStyles.WowLike);
        Assert.Contains("a Female Windshaper Skyborne Warlock", sky);
        Assert.Contains("may be a race you do not know", sky);
        Assert.DoesNotContain("anatomy, which", sky);
        Assert.Contains("Death Knight", EnhancementPrompt.Build(Troll with { Class = "DEATHKNIGHT" }, EnhanceStyles.WowLike));
    }

    [Fact]
    public void The_signature_is_the_capture_the_character_and_the_way_and_nothing_else()
    {
        var s = EnhancementSignature.Compute("g", 100, "abc", Troll, "wow-like", "gpt-6-astra", "low");
        Assert.Equal(s, EnhancementSignature.Compute("g", 100, "ABC ", Troll, "WOW-LIKE", " gpt-6-astra", "Low"));
        // Same pixels, new capture: a new attempt is owed.
        Assert.NotEqual(s, EnhancementSignature.Compute("g", 101, "abc", Troll, "wow-like", "gpt-6-astra", "low"));
        Assert.NotEqual(s, EnhancementSignature.Compute("g", null, "abc", Troll, "wow-like", "gpt-6-astra", "low"));
        Assert.NotEqual(s, EnhancementSignature.Compute("g", 100, "abd", Troll, "wow-like", "gpt-6-astra", "low"));
        Assert.NotEqual(s, EnhancementSignature.Compute("g", 100, "abc", Troll with { Class = "MAGE" }, "wow-like", "gpt-6-astra", "low"));
        Assert.NotEqual(s, EnhancementSignature.Compute("g", 100, "abc", Troll, "realistic", "gpt-6-astra", "low"));
        Assert.NotEqual(s, EnhancementSignature.Compute("g", 100, "abc", Troll, "wow-like", "other", "low"));
        Assert.NotEqual(s, EnhancementSignature.Compute("g", 100, "abc", Troll, "wow-like", "gpt-6-astra", "high"));
        // Level and name are not the picture.
        Assert.Equal(s, EnhancementSignature.Compute("g", 100, "abc", Troll with { Level = 60, Name = "Renamed", Updated = 9 }, "wow-like", "gpt-6-astra", "low"));
        Assert.Equal(64, s.Length);
    }
}

public class EnhancementCutoutTests
{
    // A picture: transparent, with an opaque figure box at (x, y, w, h) and alpha a.
    private static RgbaImage Picture(int width, int height, int x, int y, int w, int h, byte a = 255)
    {
        var img = new RgbaImage(width, height);
        for (var yy = y; yy < y + h; yy++)
            for (var xx = x; xx < x + w; xx++)
                img[xx, yy] = (200, 100, 50, a);
        return img;
    }

    [Fact]
    public void The_three_checks_are_named_and_the_bottom_may_hold_the_feet()
    {
        Assert.Null(Enhancement.Refuse(Picture(100, 200, 20, 20, 40, 160)));
        // Feet on the bottom edge: fine. Anything on the top or a side: not.
        Assert.Null(Enhancement.Refuse(Picture(100, 200, 20, 20, 40, 180)));
        Assert.Equal(Enhancement.TransparentBorder, Enhancement.Refuse(Picture(100, 200, 20, 0, 40, 160)));
        Assert.Equal(Enhancement.TransparentBorder, Enhancement.Refuse(Picture(100, 200, 0, 20, 40, 160)));
        Assert.Equal(Enhancement.TransparentBorder, Enhancement.Refuse(Picture(100, 200, 60, 20, 40, 160)));
        // A faint outlier on the border (alpha below Visible) is not the figure.
        var faint = Picture(100, 200, 20, 20, 40, 160);
        faint[0, 0] = (255, 255, 255, 7);
        Assert.Null(Enhancement.Refuse(faint));
        // Too little figure: a 4 x 4 in 100 x 200 is under 5 %.
        Assert.Equal(Enhancement.EnoughFigure, Enhancement.Refuse(Picture(100, 200, 20, 20, 4, 4)));
        // Alpha 254 counts as opaque; alpha 200 does not.
        Assert.Null(Enhancement.Refuse(Picture(100, 200, 20, 20, 40, 160, a: 254)));
        Assert.Equal(Enhancement.EnoughFigure, Enhancement.Refuse(Picture(100, 200, 20, 20, 40, 160, a: 200)));
        // Wider than 1/1.15 of its height: not a standing figure.
        Assert.Equal(Enhancement.StandingFigure, Enhancement.Refuse(Picture(200, 200, 20, 20, 160, 160)));
        Assert.Null(Enhancement.Refuse(Picture(200, 200, 20, 20, 100, 116)));
        Assert.Equal(Enhancement.StandingFigure, Enhancement.Refuse(Picture(200, 200, 20, 20, 100, 114)));
    }

    [Fact]
    public void The_cutout_is_the_figure_scaled_to_the_converter_s_height_on_a_power_of_two_canvas()
    {
        var png = Picture(400, 1200, 50, 100, 200, 1000);
        png[60, 110] = (1, 2, 3, 128);
        var meta = new CutoutMeta { Guid = "g", Epoch = 5, Shots = ["a", "b"], NativePx = [300, 900] };
        var cut = Enhancement.ToCutout(png, meta);
        Assert.Equal((CutoutConverter.TargetHeight, 102), (cut.Meta.H, cut.Meta.W));
        Assert.Equal((128, 512), (cut.Meta.TexW, cut.Meta.TexH));
        Assert.Equal((128, 512), (cut.Canvas.Width, cut.Canvas.Height));
        // The capture's identity stays; the screenshots are not this file's.
        Assert.Equal(("g", 5L), (cut.Meta.Guid, cut.Meta.Epoch));
        Assert.Null(cut.Meta.Shots);
        Assert.Equal([300, 900], cut.Meta.NativePx!);
        // Partial alpha survives the resampler (it is not thresholded away).
        Assert.Contains(cut.Canvas.Pixels.Where((_, i) => i % 4 == 3), a => a is > 8 and < 250);
        var why = Assert.Throws<ThumbnailException>(() => Enhancement.ToCutout(Picture(100, 200, 0, 0, 100, 200), meta));
        Assert.Equal(Enhancement.TransparentBorder, why.Message);
    }
}

public class AttemptHistoryTests
{
    private static readonly DateTime T0 = new(2026, 9, 30, 1, 0, 0);

    [Fact]
    public void The_enhancement_block_survives_the_sidecar()
    {
        using var t = new TempInstall();
        var f = new CutoutFolder(Path.Combine(t.Root, "addon"));
        var meta = new CutoutMeta
        {
            W = 4, H = 4, TexW = 4, TexH = 4, Guid = "Player-1-AAAA", Epoch = 7,
            Enhancement = new EnhancementMeta
            {
                SourceHash = "abc", OutputHash = "def", Style = "wow-like", Model = "gpt-6-astra", Effort = "low",
                Prompt = 1, Signature = "sig", Generated = new DateTime(2026, 9, 30, 1, 2, 3, DateTimeKind.Utc),
            },
        };
        f.WriteCutout("aaa", TestData.Solid(4, 4, 1, 1, 1), meta);
        var back = f.ReadMeta("aaa")!;
        Assert.Equal(meta.Enhancement, back.Enhancement);
        Assert.Equal(7, back.Epoch);
        // A plain cutout has no block, and writes none.
        f.WriteCutout("bbb", TestData.Solid(4, 4, 1, 1, 1), meta with { Enhancement = null });
        Assert.Null(f.ReadMeta("bbb")!.Enhancement);
        Assert.DoesNotContain("enhancement", File.ReadAllText(Path.Combine(f.CutoutsDir, "bbb.json")));
    }

    [Fact]
    public void An_attempt_is_written_before_the_launch_and_a_signature_is_never_launched_twice()
    {
        using var t = new TempInstall();
        var dir = Path.Combine(t.Root, "Enhanced");
        var h = AttemptHistory.Load(dir, "Player-1-AAAA");
        Assert.False(h.Has("sig1"));
        h.Begin(new Attempt { Signature = "sig1", Base = "aaa", Started = T0, Style = "wow-like" });
        // On disk already, as unknown: a crash from here on still counts.
        var again = AttemptHistory.Load(dir, "Player-1-AAAA");
        Assert.True(again.Has("sig1"));
        Assert.Equal(Attempt.Unknown, again.Last!.Outcome);
        Assert.Throws<AttemptHistoryException>(() => again.Begin(new Attempt { Signature = "sig1", Started = T0 }));

        h.End("sig1", Attempt.Written, T0.AddMinutes(2), "out1");
        h.Begin(new Attempt { Signature = "sig2", Started = T0.AddMinutes(3) });
        h.End("sig2", Attempt.Refused + ":" + Enhancement.StandingFigure, T0.AddMinutes(5));
        var third = AttemptHistory.Load(dir, "Player-1-AAAA");
        Assert.Equal(["sig1", "sig2"], third.Attempts.Select(a => a.Signature));
        // A -> B -> A: A is still recorded, so nothing is launched; and what is on disk is A's.
        Assert.True(third.Has("sig1"));
        Assert.False(third.MayLaunch("sig1"));
        Assert.False(third.MayLaunch("sig2"));
        Assert.Equal("out1", third.LastWritten!.OutputHash);
        Assert.Equal("sig2", third.Last!.Signature);

        // A cancel is not the picture's fault: once more, and once only. The second attempt is
        // its own record, ended on its own.
        third.Begin(new Attempt { Signature = "sig3", Started = T0.AddMinutes(6) });
        Assert.False(third.MayLaunch("sig3"));                       // in flight: not while open
        third.End("sig3", Attempt.Cancelled, T0.AddMinutes(7));
        Assert.True(third.MayLaunch("sig3"));
        third.Begin(new Attempt { Signature = "sig3", Started = T0.AddMinutes(8) });
        third.End("sig3", Attempt.Cancelled + ": the app is stopping", T0.AddMinutes(9));
        Assert.Equal([Attempt.Cancelled, Attempt.Cancelled + ": the app is stopping"], third.Attempts.Where(a => a.Signature == "sig3").Select(a => a.Outcome));
        Assert.False(third.MayLaunch("sig3"));
        Assert.Throws<AttemptHistoryException>(() => third.Begin(new Attempt { Signature = "sig3", Started = T0 }));
    }

    [Fact]
    public void Not_knowing_is_not_permission()
    {
        using var t = new TempInstall();
        var dir = Path.Combine(t.Root, "Enhanced");
        Directory.CreateDirectory(dir);
        File.WriteAllText(AttemptHistory.PathFor(dir, "Player-1-AAAA"), "{ not json");
        Assert.Throws<AttemptHistoryException>(() => AttemptHistory.Load(dir, "Player-1-AAAA"));
        File.WriteAllText(AttemptHistory.PathFor(dir, "Player-1-BBBB"), "{ \"Guid\": \"Player-1-OTHER\", \"Attempts\": [] }");
        Assert.Throws<AttemptHistoryException>(() => AttemptHistory.Load(dir, "Player-1-BBBB"));
        File.WriteAllText(AttemptHistory.PathFor(dir, "Player-1-CCCC"), "{ \"Guid\": \"Player-1-CCCC\", \"Attempts\": [ { \"Outcome\": \"written\" } ] }");
        Assert.Throws<AttemptHistoryException>(() => AttemptHistory.Load(dir, "Player-1-CCCC"));
        File.WriteAllText(AttemptHistory.PathFor(dir, "Player-1-EEEE"), "{ \"Guid\": \"Player-1-EEEE\", \"Attempts\": [ null ] }");
        Assert.Throws<AttemptHistoryException>(() => AttemptHistory.Load(dir, "Player-1-EEEE"));
        // Refused, and left as it was: the history is never rewritten by a reader.
        Assert.Contains("null", File.ReadAllText(AttemptHistory.PathFor(dir, "Player-1-EEEE")));
        // A record that cannot be written refuses the launch: the folder is a file.
        var blocked = Path.Combine(t.Root, "blocked");
        File.WriteAllText(blocked, "");
        var h = AttemptHistory.Load(blocked, "Player-1-DDDD");
        Assert.Throws<AttemptHistoryException>(() => h.Begin(new Attempt { Signature = "s", Started = T0 }));
        // Not written is not attempted: nothing lingers in memory to refuse the next try.
        Assert.False(h.Has("s"));
        Assert.Empty(h.Attempts);
        Assert.Throws<ArgumentException>(() => AttemptHistory.Load(dir, "not a guid!"));
    }
}
