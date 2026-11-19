using System.Globalization;
using System.Text.Json.Nodes;

namespace AltStableCompanion.Core.Tests;

public sealed class SheetExportTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("asc-sheet-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Store(string account, string text)
    {
        var path = Path.Combine(_dir, account, "SavedVariables", SavedVariablesReader.FileName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    internal static string Record(string guid, params (string Key, object Value)[] fields) =>
        $"[\"{guid}\"] = {{\n" + string.Concat(fields.Select(f => f.Value is string s
            ? $"[\"{f.Key}\"] = \"{s}\",\n"
            : $"[\"{f.Key}\"] = {Convert.ToString(f.Value, CultureInfo.InvariantCulture)},\n")) + "},\n";

    // A whole file: the addon always writes both tables.
    internal static string Db(params string[] records) => "AltStableDB = {\n" + string.Concat(records) + "}\nAltStableConfig = {\n}\n";

    private static Dictionary<object, object?> Char(params (string Key, object? Value)[] fields) =>
        fields.ToDictionary(f => (object)f.Key, f => f.Value is int i ? (double)i : f.Value);

    [Fact]
    public void Two_accounts_holding_one_character_give_one_row_the_newer_record()
    {
        var a = Store("A", Db(Record("Player-1-01", ("name", "Karuzo"), ("level", 20), ("lastUpdate", 200))));
        var b = Store("B", Db(Record("Player-1-01", ("name", "Karuzo"), ("level", 19), ("lastUpdate", 100))));

        foreach (var order in new[] { new[] { a, b }, new[] { b, a } })
        {
            var roster = SheetExport.Load(order);
            var only = Assert.Single(roster.Characters);
            Assert.Equal(20.0, only["level"]);
        }
    }

    [Fact]
    public void A_character_hidden_in_any_account_is_left_out()
    {
        var a = Store("A", Db(Record("Player-1-01", ("name", "Shown")), Record("Player-1-02", ("name", "Hidden"))));
        var b = Store("B", "AltStableDB = {\n}\nAltStableConfig = {\n[\"hiddenCharacters\"] = {\n[\"Player-1-02\"] = true,\n},\n}\n");

        var roster = SheetExport.Load([a, b]);

        Assert.Equal(["Shown"], roster.Characters.Select(c => c["name"]));
    }

    [Fact]
    public void A_file_cut_mid_table_is_skipped_by_name_so_nothing_is_written()
    {
        var good = Store("A", Db(Record("Player-1-01", ("name", "Karuzo"))));
        var cut = Store("B", "AltStableDB = {\n[\"Player-1-02\"] = {\n[\"name\"] = \"Elegia\",\n");

        var roster = SheetExport.Load([good, cut]);

        Assert.Equal([cut], roster.Skipped);
    }

    [Fact]
    public void A_folder_with_no_AltStable_account_is_not_an_empty_roster()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "C", "SavedVariables"));      // an account that never ran AltStable

        foreach (var dir in new[] { _dir, Path.Combine(_dir, "typo") })
        {
            var roster = SheetExport.Load(dir);
            Assert.True(roster.NoAccounts);
            Assert.False(roster.Complete);
        }
    }

    [Fact]
    public void An_account_with_an_empty_roster_is_a_complete_empty_export()
    {
        Store("A", Db());

        var roster = SheetExport.Load(_dir);

        Assert.True(roster.Complete);
        Assert.Empty(roster.Characters);
    }

    [Fact]
    public void An_account_whose_file_is_only_its_bak_right_now_is_skipped_not_left_out()
    {
        Store("A", Db(Record("Player-1-01", ("name", "Karuzo"))));
        var bak = Path.Combine(_dir, "B", "SavedVariables", "AltStable.lua.bak");
        Directory.CreateDirectory(Path.GetDirectoryName(bak)!);
        File.WriteAllText(bak, Db(Record("Player-1-02", ("name", "Elegia"))));
        Directory.CreateDirectory(Path.Combine(_dir, "C", "SavedVariables"));      // an account that never ran AltStable

        var roster = SheetExport.Load(_dir);

        Assert.Equal([Path.Combine(_dir, "B", "SavedVariables", SavedVariablesReader.FileName)], roster.Skipped);
    }

    [Theory]
    [InlineData("AltStableConfig = {\n}\n")]                                                  // no roster
    [InlineData("AltStableDB = {\n[\"Player-1-02\"] = {\n[\"name\"] = \"Elegia\",\n},\n}\n")]  // no config: its hidden list unknown
    [InlineData("")]
    public void A_file_missing_either_table_is_skipped(string text)
    {
        Store("A", Db(Record("Player-1-01", ("name", "Karuzo"))));
        var odd = Store("B", text);

        Assert.Equal([odd], SheetExport.Load(_dir).Skipped);
    }

    [Theory]
    [InlineData(40, "true", 8, 45)]          // 5% per 8 h in a rested area
    [InlineData(40, null, 32, 45)]           // ... per 32 h outside
    [InlineData(40, "false", 32, 45)]        // a synced alt's "false" is a string, not a rested area
    [InlineData(140, "true", 100, 150)]      // the cap
    [InlineData(0, "true", 0, 0)]
    public void Rested_carries_forward_from_the_snapshot_as_the_addon_does(int stored, string? area, int hours, int expected)
    {
        var c = Char(("restPercent", stored), ("xpMax", 1000), ("restTimestamp", 1000));
        if (area is not null) c["restedArea"] = area;

        Assert.Equal(expected, SheetExport.RestedPercent(c, 1000 + hours * 3600L));
    }

    [Fact]
    public void Rested_is_blank_without_a_snapshot_zero_at_the_level_cap_and_higher_with_well_rested()
    {
        Assert.Null(SheetExport.RestedPercent(Char(("level", 30)), 999_999));
        Assert.Equal(0, SheetExport.RestedPercent(Char(("restPercent", 0), ("xpMax", 0), ("restTimestamp", 0)), 999_999));
        Assert.Equal(180, SheetExport.RestedPercent(Char(("restPercent", 100), ("xpMax", 1000), ("restTimestamp", 0), ("wellRested", 1)), 10_000_000));
        // A record from before xpMax was kept is still carried forward.
        Assert.Equal(45, SheetExport.RestedPercent(Char(("restPercent", 40), ("restTimestamp", 0), ("restedArea", true)), 8 * 3600));
    }

    [Fact]
    public void Rows_go_Horde_then_Alliance_then_by_level_down()
    {
        var chars = new List<Dictionary<object, object?>>
        {
            Char(("name", "A10"), ("faction", "Alliance"), ("level", 10)),
            Char(("name", "H5"), ("faction", "Horde"), ("level", 5)),
            Char(("name", "A30"), ("faction", "Alliance"), ("level", 30)),
            Char(("name", "H20"), ("faction", "Horde"), ("level", 20)),
        };
        chars.Sort(SheetExport.Order);

        Assert.Equal(["H20", "H5", "A30", "A10"], chars.Select(c => c["name"]));
    }

    // A character's row: the one whose first cell is its name.
    private static IReadOnlyList<SheetCell> RowOf(SheetTable t, string name) => t.Rows.Single(r => r[0].Value as string == name);

    // A column by its header: a label, or an icon's note.
    private static int Col(SheetTable t, string label)
    {
        var header = t.Rows.First(r => r[0].Value as string == "Character");
        var at = header.ToList().FindIndex(c => c.Value as string == label || c.Note == label || (c.Note?.StartsWith(label + ":", StringComparison.Ordinal) ?? false));
        Assert.True(at >= 0, "no column " + label);
        return at;
    }

    private static string? ImageOf(SheetCell c) => c.Value is SheetImage i ? i.Url : null;

    [Fact]
    public void A_tab_is_the_last_update_line_then_a_section_per_side()
    {
        var chars = new List<Dictionary<object, object?>>
        {
            Char(("name", "H1"), ("faction", "Horde"), ("class", "MAGE"), ("level", 20)),
            Char(("name", "A1"), ("faction", "Alliance"), ("class", "PRIEST"), ("level", 10)),
        };
        var now = 1795044105L;

        foreach (var t in SheetExport.Build(chars, now))
        {
            Assert.Equal("Last update: " + DateTimeOffset.FromUnixTimeSeconds(now).ToLocalTime().ToString("yyyy-MM-dd HH:mm"), t.Rows[0][0].Value);
            Assert.Equal(["Horde", "Character", "H1", "Alliance", "Character", "A1"], t.Rows.Skip(1).Select(r => r[0].Value as string));
            Assert.EndsWith("ui_hordeicon.jpg", ImageOf(t.Rows[1][1]));
            Assert.EndsWith("ui_allianceicon.jpg", ImageOf(t.Rows[4][1]));
            Assert.Equal(1, t.FrozenRows);
            Assert.Equal(3, t.FrozenColumns);
            Assert.Equal(SheetExport.AvatarSize, t.RowHeights![3]);
            Assert.Equal(SheetExport.IconSize, t.RowHeights[1]);
            // Every cell coloured, rows as long as the header: bands of colour, no gaps.
            Assert.All(t.Rows, r => Assert.Equal(t.Columns, r.Count));
            Assert.All(t.Rows.SelectMany(r => r), c => Assert.NotNull(c.Fill));
            // Every column has a width: Sheets' own fit cuts bold text short.
            Assert.All(t.ColumnWidths!, w => Assert.NotNull(w));
            Assert.Equal(t.Columns, t.ColumnWidths!.Count);
            // The "Last update" line runs on into the cells beside it: it does not widen Name.
            Assert.Equal(SheetExport.TextWidth(t.Rows.Skip(1), 0), t.ColumnWidths[0]);
            Assert.True(t.ColumnWidths[0] < SheetExport.TextWidth(t.Rows, 0));
        }
        Assert.Equal([SheetExport.Characters, SheetExport.Skills, SheetExport.Reputations, SheetExport.Gear],
            SheetExport.Build(chars, now).Select(t => t.Title));
    }

    [Fact]
    public void Characters_has_gold_in_coins_the_class_colour_and_icon_the_hearth_and_the_ruleset()
    {
        var table = SheetExport.BuildCharacters([
            Char(("name", "K"), ("class", "SHAMAN"), ("faction", "Horde"), ("money", 102690), ("bind", "Booty Bay"),
                ("realm", "CAMWOW US2 Realm 1 PvE"), ("accountNumber", 2)),
            Char(("name", "L"), ("class", "MAGE"), ("faction", "Horde"), ("money", 9))], 0);

        var k = RowOf(table, "K");
        var gold = Col(table, "Gold");
        Assert.Equal([10.0, 26.0, 90.0], new[] { k[gold].Value, k[gold + 2].Value, k[gold + 4].Value });
        Assert.EndsWith("inv_misc_coin_01.jpg", ImageOf(k[gold + 1]));
        Assert.EndsWith("inv_misc_coin_05.jpg", ImageOf(k[gold + 5]));
        // The side's gold on its banner.
        var banner = table.Rows[1];
        Assert.Equal([10.0, 26.0, 99.0], new[] { banner[gold].Value, banner[gold + 2].Value, banner[gold + 4].Value });
        Assert.Equal("0070DD", k[0].Fill);
        Assert.Equal("0070DD", k[gold].Fill);
        Assert.EndsWith("classicon_shaman.jpg", ImageOf(k[2]));
        Assert.Equal("Shaman", k[2].Note);
        Assert.Equal("Booty Bay", k[Col(table, "Hearthstone")].Value);
        Assert.Equal(2.0, k[Col(table, "Acc")].Value);
        var ruleset = k[Col(table, "Ruleset")];
        Assert.Equal("PvE", ruleset.Value);
        Assert.Equal("CAMWOW US2 Realm 1 PvE", ruleset.Note);
    }

    [Theory]
    [InlineData("CAMWOW US2 Realm 1 PvE", "PvE")]
    [InlineData("Some Realm RP-PvP", "RP-PvP")]
    [InlineData("Hardcore Land hardcore", "hardcore")]
    [InlineData("Nameless", "Nameless")]
    public void The_ruleset_is_the_realms_last_word_when_it_is_one(string realm, string expected) =>
        Assert.Equal(expected, SheetExport.Ruleset(realm));

    [Fact]
    public void Accounts_are_numbered_in_their_folders_order_never_named()
    {
        Store("ZETA#1", Db(Record("Player-1-01", ("name", "Late"))));
        Store("ALPHA#1", Db(Record("Player-1-02", ("name", "Early"))));

        var chars = SheetExport.Load(_dir).Characters;

        Assert.Equal(1.0, chars.Single(c => (string)c["name"]! == "Early")["accountNumber"]);
        Assert.Equal(2.0, chars.Single(c => (string)c["name"]! == "Late")["accountNumber"]);
        Assert.DoesNotContain(SheetExport.Build(chars, 0).SelectMany(t => t.Rows).SelectMany(r => r),
            c => (c.Value as string ?? c.Note ?? "").Contains('#'));
    }

    [Fact]
    public void A_skill_at_its_cap_is_highlighted_one_below_is_not_and_zero_is_empty()
    {
        var table = SheetExport.BuildSkills([Char(("name", "K"), ("class", "MAGE"), ("level", 30),
            ("prof_Enchanting", 150), ("profmax_Enchanting", 150),
            ("prof_Tailoring", 149), ("profmax_Tailoring", 150),
            ("fishing", 0), ("fishingMax", 75))]);

        var k = RowOf(table, "K");
        Assert.Equal(150.0, k[Col(table, "Enchanting")].Value);
        Assert.Equal("FFF200", k[Col(table, "Enchanting")].Fill);
        Assert.Equal("3FC7EB", k[Col(table, "Tailoring")].Fill);        // the row's class colour
        Assert.Equal("149 / 150", k[Col(table, "Tailoring")].Note);
        Assert.Null(k[Col(table, "Fishing")].Value);
        var header = table.Rows.First(r => r[0].Value as string == "Character");
        Assert.EndsWith("trade_engraving.jpg", ImageOf(header[Col(table, "Enchanting")]));
    }

    [Fact]
    public void Reputations_are_the_factions_met_as_letters_in_their_standing_colour_a_pair_in_one_column()
    {
        var table = SheetExport.BuildReputations([
            Char(("name", "K"), ("class", "MAGE"), ("faction", "Horde"), ("rep_76", 8), ("rep_2778", 5), ("rep_529", 9)),
            Char(("name", "L"), ("class", "MAGE"), ("faction", "Horde"), ("rep_76", 1)),
            Char(("name", "A"), ("class", "MAGE"), ("faction", "Alliance"), ("rep_72", 7)),
            Char(("name", "B"), ("class", "MAGE"), ("faction", "Alliance"), ("rep_76", 5))]);   // only the other side's

        // Stormwind/Orgrimmar and High Order/Windshapers: one column each. A standing out of range is none.
        Assert.Equal(Lead + 2, table.Columns);
        var headers = table.Rows.Where(r => r[0].Value as string == "Character").ToList();
        Assert.EndsWith("inv_misc_tournaments_tabard_orc.jpg", ImageOf(headers[0][Lead]));        // the Horde section's
        Assert.EndsWith("inv_misc_tournaments_tabard_human.jpg", ImageOf(headers[1][Lead]));      // the Alliance section's
        Assert.Equal("Stormwind / Orgrimmar", headers[0][Lead].Note);
        Assert.Equal("Windsh", headers[0][Lead + 1].Value);
        Assert.Equal("HiOrdr", headers[1][Lead + 1].Value);

        var k = RowOf(table, "K")[Lead];
        Assert.Equal("E", k.Value);
        Assert.Equal("00FFFF", k.Fill);
        Assert.Equal("Orgrimmar: Exalted", k.Note);
        Assert.Equal("Ha", RowOf(table, "L")[Lead].Value);
        Assert.Equal("Stormwind: Revered", RowOf(table, "A")[Lead].Note);
        Assert.Equal("Orgrimmar: Friendly", RowOf(table, "B")[Lead].Note);
        Assert.Equal("Windshapers: Friendly", RowOf(table, "K")[Lead + 1].Note);
        Assert.Null(RowOf(table, "A")[Lead + 1].Value);
    }

    [Fact]
    public void Every_reputation_is_in_exactly_one_column_in_the_addons_order()
    {
        var columns = SheetExport.RepColumns();

        Assert.Equal(SheetExport.AllReputations.Select(r => r.Id).Order(), columns.SelectMany(c => c.All).Select(r => r.Id).Order());
        Assert.Equal(SheetExport.AllReputations.Length - SheetExport.RepPairs.Length, columns.Count);
        Assert.Equal((72, 76), (columns[0].Alliance!.Id, columns[0].Horde!.Id));
    }

    private const int Lead = 3;

    [Fact]
    public void Gear_is_each_slots_item_level_in_its_quality_colour_then_the_enchants()
    {
        var table = SheetExport.BuildGear([Char(("name", "K"), ("class", "MAGE"), ("ilvl", 27.24),
            ("gearname_chest", "Civinad Robes"), ("gearq_chest", 3), ("gear_chest", 31), ("gearid_chest", 9623),
            ("gearench_chest", "Stamina +2"),
            ("gearname_feet", ""), ("gearid_feet", 9454), ("gearq_feet", 2),
            ("gearname_head", ""), ("gearid_head", 0))]);

        var k = RowOf(table, "K");
        Assert.Equal(27.2, k[Col(table, "iLvl")].Value);
        Assert.Equal(31.0, k[Col(table, "Chest")].Value);
        Assert.Equal("0070DD", k[Col(table, "Chest")].Color);
        Assert.Equal("Civinad Robes\nEnchanted: Stamina +2", k[Col(table, "Chest")].Note);
        Assert.Equal("?", k[Col(table, "Feet")].Value);          // worn, its level not known
        Assert.Equal("item 9454", k[Col(table, "Feet")].Note);   // its name not cached at scan time
        Assert.Null(k[Col(table, "Head")].Value);                // nothing worn
        // Each item links to its Wowhead Forever page: its tooltip, a click away.
        Assert.Equal("https://www.wowhead.com/forever/item=9623", k[Col(table, "Chest")].Link);
        Assert.Equal("https://www.wowhead.com/forever/item=9454", k[Col(table, "Feet")].Link);
        Assert.Null(k[Col(table, "Head")].Link);
        Assert.Equal("Stamina +2", k[Col(table, "Chest enchant")].Value);
        // Only the slots someone has an enchant on.
        Assert.Equal(Lead + 1 + SheetExport.GearSlots.Length + 1, table.Columns);
    }

    [Fact]
    public void A_sheet_date_is_days_since_1899_12_30_in_local_time()
    {
        var epoch = 1795044105L;
        var local = DateTimeOffset.FromUnixTimeSeconds(epoch).ToLocalTime().DateTime;

        Assert.Equal(local, DateTime.FromOADate(SheetExport.SheetDate(epoch)), TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void A_text_column_is_as_wide_as_its_longest_text()
    {
        List<IReadOnlyList<SheetCell>> rows =
        [
            [new SheetCell("Windshaper Skyborne")], [new SheetCell("Orc")], [new SheetCell(1234567.0)],
            [new SheetCell(1.0, NumberFormat: "yyyy-mm-dd hh:mm")],
        ];

        Assert.Equal((int)Math.Ceiling(19 * 7.5) + 16, SheetExport.TextWidth(rows, 0));
        Assert.Equal((int)Math.Ceiling(16 * 7.5) + 16, SheetExport.TextWidth(rows.Skip(3), 0));
        Assert.Equal((int)Math.Ceiling(9 * 7.5) + 16, SheetExport.TextWidth(rows.Skip(2).Take(1), 0));   // 1,234,567
        Assert.Equal(32, SheetExport.TextWidth([[SheetCell.Empty]], 0));
    }

    [Fact]
    public void The_write_clears_the_whole_tab_sizes_its_grid_to_it_and_freezes_its_rows_and_columns()
    {
        var table = new SheetTable("Skills", [[new SheetCell("Name"), new SheetCell("Level")], [new SheetCell("K", "FF7C0A"), new SheetCell(30.0, Center: true)]],
            FrozenColumns: 1, FrozenRows: 1);

        var requests = GoogleSheets.BuildWriteRequests([table], new Dictionary<string, long> { ["Skills"] = 7 });

        var grid = requests[0]!["updateSheetProperties"]!["properties"]!["gridProperties"]!;
        Assert.Equal(2, (int)grid["rowCount"]!);          // no spare row under the dark
        Assert.Equal(2, (int)grid["columnCount"]!);
        Assert.Equal(1, (int)grid["frozenRowCount"]!);
        Assert.Equal(1, (int)grid["frozenColumnCount"]!);
        Assert.True((bool)grid["hideGridlines"]!);
        var cells = requests[1]!["updateCells"]!;
        // The range is the whole tab, nothing narrower: a cell the table no longer has is cleared.
        Assert.Equal(["sheetId"], cells["range"]!.AsObject().Select(p => p.Key));
        Assert.Equal(7, (long)cells["range"]!["sheetId"]!);
        var name = cells["rows"]![1]!["values"]![0]!;
        Assert.Equal("K", (string)name["userEnteredValue"]!["stringValue"]!);
        Assert.Equal(1.0, (double)name["userEnteredFormat"]!["backgroundColor"]!["red"]!);
        Assert.Equal(124 / 255.0, (double)name["userEnteredFormat"]!["backgroundColor"]!["green"]!, 9);
        Assert.Equal("MIDDLE", (string)name["userEnteredFormat"]!["verticalAlignment"]!);
        Assert.Null(name["userEnteredFormat"]!["horizontalAlignment"]);
        var level = cells["rows"]![1]!["values"]![1]!;
        Assert.Equal(30.0, (double)level["userEnteredValue"]!["numberValue"]!);
        Assert.Equal("CENTER", (string)level["userEnteredFormat"]!["horizontalAlignment"]!);
        Assert.Null(level["userEnteredFormat"]!["textFormat"]);

        var linked = GoogleSheets.BuildWriteRequests([new SheetTable("Gear", [[new SheetCell(31.0, Link: "https://x/item=1")]])],
            new Dictionary<string, long> { ["Gear"] = 7 })[1]!["updateCells"]!["rows"]![0]!["values"]![0]!;
        Assert.Equal(31.0, (double)linked["userEnteredValue"]!["numberValue"]!);       // still a number
        Assert.Equal("https://x/item=1", (string)linked["userEnteredFormat"]!["textFormat"]!["link"]!["uri"]!);
    }

    [Fact]
    public void One_row_is_frozen_never_all_of_them()
    {
        var requests = GoogleSheets.BuildWriteRequests([new SheetTable("Skills", [[new SheetCell("Last update")]], FrozenRows: 1)],
            new Dictionary<string, long> { ["Skills"] = 7 });

        Assert.Equal(0, (int)requests[0]!["updateSheetProperties"]!["properties"]!["gridProperties"]!["frozenRowCount"]!);
    }

    [Fact]
    public void Rows_get_their_heights_and_columns_their_widths_in_runs()
    {
        var table = new SheetTable("Characters",
            [[new SheetCell("a"), new SheetCell("b"), new SheetCell("c")], [SheetCell.Empty], [new SheetCell(new SheetImage("https://x/a.jpg"))], [SheetCell.Empty]],
            RowHeights: [null, 36, 42, 42], ColumnWidths: [null, 42, 42]);

        var requests = GoogleSheets.BuildWriteRequests([table], new Dictionary<string, long> { ["Characters"] = 3 });

        var image = requests[1]!["updateCells"]!["rows"]![2]!["values"]![0]!;
        Assert.Equal("=IMAGE(\"https://x/a.jpg\")", (string)image["userEnteredValue"]!["formulaValue"]!);
        var sizes = requests.Select(r => r!["updateDimensionProperties"]).OfType<JsonNode>()
            .Select(d => ((string)d["range"]!["dimension"]!, (int)d["range"]!["startIndex"]!, (int)d["range"]!["endIndex"]!, (int)d["properties"]!["pixelSize"]!))
            .ToList();
        // The default where the table says none: a row that was tall is not left tall.
        Assert.Equal([("ROWS", 0, 1, GoogleSheets.DefaultRowHeight), ("ROWS", 1, 2, 36), ("ROWS", 2, 4, 42), ("COLUMNS", 1, 3, 42)], sizes);
        var fit = requests.Select(r => r!["autoResizeDimensions"]).OfType<JsonNode>().Single();
        Assert.Equal((0, 1), ((int)fit["dimensions"]!["startIndex"]!, (int)fit["dimensions"]!["endIndex"]!));
    }

    [Fact]
    public void The_avatar_is_the_armory_one_by_player_number_hd_or_sd()
    {
        // Measured 2026-11-19: 678282 (0x000A598A) has both, 84x84 JPEGs.
        Assert.Equal("https://render.worldofwarcraft.com/forever-us/character/138/678282-avatar.jpg",
            Armory.AvatarUrl("Player-6796-000A598A", sd: false));
        Assert.Equal("https://render.worldofwarcraft.com/forever-us/character/138/678282-avatar-sd.jpg",
            Armory.AvatarUrl("Player-6796-000A598A", sd: true));
        Assert.Null(Armory.AvatarUrl("Creature-0-1-2", sd: false));
    }

    [Theory]
    [InlineData("Orc", "Horde", "Male", 0, "2-0")]
    [InlineData("Scourge", "Horde", "Female", 1, "5-1")]
    [InlineData("Skyborne", "Alliance", "Female", 1, "95-1")]     // High Order
    [InlineData("Skyborne", "Horde", "Male", 0, "96-0")]          // Windshaper
    public void The_placeholder_is_the_armory_silhouette_of_the_race_and_sex(string race, string faction, string gender, int sexId, string file)
    {
        // Measured 2026-11-19: forever-us/shadow/avatar/<ChrRaces id>-<0 male, 1 female>.jpg, all ten.
        var expected = "https://render.worldofwarcraft.com/forever-us/shadow/avatar/" + file + ".jpg";
        Assert.Equal(expected, Armory.PlaceholderUrl(race, faction, female: sexId == 1));

        // sexID first; an older record has only the word.
        foreach (var fields in new[] { new (string, object)[] { ("sexID", sexId), ("gender", "nonsense") }, [("gender", gender)] })
        {
            var path = Store("S" + file + fields.Length, Db(Record("Player-1-01",
                [("name", "Low"), ("level", 5), ("race", race), ("faction", faction), .. fields])));
            var table = SheetExport.BuildCharacters(SheetExport.Load([path]).Characters, 0, new AvatarSource(false, new HashSet<string>()));
            Assert.Equal(expected, ImageOf(RowOf(table, "Low")[1]));
        }
    }

    [Fact]
    public void A_race_the_armory_has_no_silhouette_for_gets_no_picture()
    {
        Assert.Null(Armory.PlaceholderUrl("Skyborne", "Neutral", female: false));
        Assert.Null(Armory.PlaceholderUrl("Vulpera", "Horde", female: false));
        Assert.Null(Armory.PlaceholderUrl(null, "Horde", female: false));
    }

    [Fact]
    public void With_avatars_a_drawn_character_gets_its_own_and_everyone_else_the_silhouette()
    {
        var path = Store("A", Db(
            Record("Player-1-0000000A", ("name", "Drawn"), ("level", 10), ("race", "Orc"), ("sexID", 1)),
            Record("Player-1-0000000B", ("name", "Undrawn"), ("level", 30), ("race", "Orc"), ("sexID", 0)),
            Record("Player-1-00000009", ("name", "Nine"), ("level", 9), ("race", "Troll"), ("sexID", 1))));
        var chars = SheetExport.Load([path]).Characters;
        // A render under level 10 is not one the armory makes: a leftover is not trusted.
        var drawn = new HashSet<string> { "Player-1-0000000A", "Player-1-00000009" };

        foreach (var table in SheetExport.Build(chars, 0, new AvatarSource(true, drawn)))
        {
            // The record's key is the character's identity, and SD is the model asked for.
            Assert.Equal(Armory.AvatarUrl("Player-1-0000000A", sd: true), ImageOf(RowOf(table, "Drawn")[1]));
            Assert.Equal(Armory.PlaceholderUrl("Orc", null, female: false), ImageOf(RowOf(table, "Undrawn")[1]));
            Assert.Equal(Armory.PlaceholderUrl("Troll", null, female: true), ImageOf(RowOf(table, "Nine")[1]));
        }

        // Without pictures the column is still there, empty: a formula's address keeps its field.
        foreach (var plain in SheetExport.Build(chars, 0))
        {
            Assert.All(chars, c => Assert.Null(RowOf(plain, (string)c["name"]!)[1].Value));
            Assert.DoesNotContain(plain.Rows.SelectMany(r => r), c => ImageOf(c) is { } url && url.Contains("/forever-us/", StringComparison.Ordinal));
        }
    }

    private static SheetTable Table(string title) => new(title, [[new SheetCell("Name")]]);

    private static GoogleSheets.Tab Tab(long id, string title, string? tag = null) => new(id, title, tag);

    [Fact]
    public void A_tab_the_player_renamed_is_still_ours_and_their_tab_with_our_old_name_is_not()
    {
        var (ids, adds) = GoogleSheets.PlanTabs([Table("Gear")],
            [Tab(9, "Gear"), Tab(5, "Gear export", "Gear")], () => 77);

        Assert.Equal(5, ids["Gear"]);
        Assert.Empty(adds);
    }

    // A listing of tabs as Google gives it, each tab with the raw value of our tag on it, if any.
    private static JsonNode Listing(params (long Id, string Title, string? Tag)[] tabs) => new JsonObject
    {
        ["sheets"] = new JsonArray(tabs.Select(t => (JsonNode)new JsonObject
        {
            ["properties"] = new JsonObject { ["sheetId"] = t.Id, ["title"] = t.Title },
            ["developerMetadata"] = t.Tag is null ? new JsonArray()
                : new JsonArray(new JsonObject { ["metadataKey"] = GoogleSheets.TabTag, ["metadataValue"] = t.Tag }),
        }).ToArray()),
    };

    [Fact]
    public void A_copy_of_our_tab_carrying_our_tag_is_not_ours_listed_before_or_after_the_original()
    {
        var original = (5L, "Gear", (string?)"Gear#5");
        var copy = (6L, "Copy of Gear", (string?)"Gear#5");     // the tag names the tab it was put on: 5

        foreach (var listing in new[] { Listing(original, copy), Listing(copy, original) })
        {
            var (ids, adds) = GoogleSheets.PlanTabs([Table("Gear")], GoogleSheets.Tabs(listing).ToList(), () => 77);
            Assert.Equal(5, ids["Gear"]);
            Assert.Empty(adds);
        }
    }

    [Fact]
    public void Once_the_original_is_deleted_its_copy_is_still_the_players()
    {
        var (ids, adds) = GoogleSheets.PlanTabs([Table("Gear")],
            GoogleSheets.Tabs(Listing((6L, "Copy of Gear", "Gear#5"))).ToList(), () => 77);

        Assert.Equal(77, ids["Gear"]);
        Assert.Equal([(77L, "Gear", "Gear")], adds);
    }

    [Fact]
    public void A_deleted_tab_comes_back_under_a_free_name_and_a_fresh_id()
    {
        var draws = new Queue<int>([9, 9, 12]);   // 9 is taken: drawn again
        var (ids, adds) = GoogleSheets.PlanTabs([Table("Gear")],
            [Tab(9, "gear"), Tab(3, "Gear (AltStable)"), Tab(4, "Skills", "Skills")], draws.Dequeue);

        Assert.Equal(12, ids["Gear"]);
        Assert.Equal([(12L, "Gear (AltStable 2)", "Gear")], adds);
    }

    [Fact]
    public void A_tab_is_ours_by_our_tag_only()
    {
        var listing = JsonNode.Parse("""
            {"sheets":[
              {"properties":{"sheetId":1,"title":"Gear"},"developerMetadata":[{"metadataKey":"other.app","metadataValue":"Gear#1"},{"metadataKey":"altstable.tab","metadataValue":"Gear#1"}]},
              {"properties":{"sheetId":2,"title":"Skills"},"developerMetadata":[{"metadataKey":"other.app","metadataValue":"Skills"}]},
              {"properties":{"sheetId":3,"title":"Notes"}},
              {"properties":{"sheetId":4,"title":"Old"},"developerMetadata":[{"metadataKey":"altstable.tab","metadataValue":"Gear"}]},
              {"properties":{"sheetId":14,"title":"Odd"},"developerMetadata":[{"metadataKey":"altstable.tab","metadataValue":"Gear#4"}]},
              {"properties":{"sheetId":7,"title":"Odder"},"developerMetadata":[{"metadataKey":"altstable.tab","metadataValue":"Gear#71"}]}]}
            """)!;

        // 4: a tag without the tab's id; 14: "#4" is not "#14"; 7: "#71" is not "#7".
        Assert.Equal([Tab(1, "Gear", "Gear"), Tab(2, "Skills"), Tab(3, "Notes"), Tab(4, "Old"), Tab(14, "Odd"), Tab(7, "Odder")],
            GoogleSheets.Tabs(listing));
    }

    [Fact]
    public async Task A_missing_tab_is_added_and_tagged_in_the_same_batch_that_fills_it()
    {
        // The player has a "Skills" of their own: ours comes back as "Skills (AltStable)", tagged "Skills".
        var google = new FakeGoogle([Tab(1, "Characters", "Characters#1"), Tab(2, "Skills")]);

        var added = await new GoogleSheets(new HttpClient(google), new GoogleClient("id", "secret"))
            .WriteAsync("token", "sheet", [Table("Characters"), Table("Skills")], CancellationToken.None);

        Assert.Equal(["Skills (AltStable)"], added);
        var requests = Assert.Single(google.Batches);
        var newId = (long)requests[0]!["addSheet"]!["properties"]!["sheetId"]!;
        var tag = requests[1]!["createDeveloperMetadata"]!["developerMetadata"]!;
        Assert.Equal(GoogleSheets.TabTag, (string)tag["metadataKey"]!);
        Assert.Equal($"Skills#{newId}", (string)tag["metadataValue"]!);
        Assert.Equal(newId, (long)tag["location"]!["sheetId"]!);
        Assert.Contains(requests, r => r!["updateCells"]?["range"]?["sheetId"] is JsonNode id && (long)id == newId);
        Assert.Contains(requests, r => r!["updateCells"]?["range"]?["sheetId"] is JsonNode id && (long)id == 1);
        Assert.DoesNotContain(requests, r => r!["updateCells"]?["range"]?["sheetId"] is JsonNode id && (long)id == 2);
    }

    [Fact]
    public async Task A_batch_Google_applied_but_whose_answer_was_lost_is_not_redone()
    {
        var google = new FakeGoogle([Tab(1, "Characters", "Characters#1")]) { LoseNextAnswer = true };
        var sheets = new GoogleSheets(new HttpClient(google), new GoogleClient("id", "secret"));
        SheetTable[] tables = [Table("Characters"), Table("Skills")];

        await Assert.ThrowsAsync<HttpRequestException>(() => sheets.WriteAsync("token", "sheet", tables, CancellationToken.None));
        var added = await sheets.WriteAsync("token", "sheet", tables, CancellationToken.None);

        Assert.Empty(added);
        Assert.Equal(2, google.State.Count);    // Characters and the one Skills: no "Skills (AltStable)"
    }

    [Theory]
    [InlineData(null)]              // made before the tags
    [InlineData("Characters")]      // tagged before a tag named its tab's id
    public async Task A_sheet_holding_none_of_our_tabs_is_refused_before_anything_is_written(string? oldTag)
    {
        var google = new FakeGoogle([Tab(1, "Characters", oldTag), Tab(2, "Gear"), Tab(3, "Skills")]);
        var sheets = new GoogleSheets(new HttpClient(google), new GoogleClient("id", "secret"));

        await Assert.ThrowsAsync<UnownedSheetException>(() =>
            sheets.WriteAsync("token", "sheet", [Table("Characters"), Table("Gear"), Table("Skills")], CancellationToken.None));

        Assert.Empty(google.Batches);
    }

    /// <summary>
    /// The Sheets API as far as the export uses it: a listing of tabs with our tags, and a
    /// batch that applies addSheet and createDeveloperMetadata to that listing. A tab's Tag here
    /// is the raw metadata value, as Google keeps it.
    /// </summary>
    private sealed class FakeGoogle(IEnumerable<GoogleSheets.Tab> tabs) : HttpMessageHandler
    {
        public List<GoogleSheets.Tab> State { get; } = tabs.ToList();
        public List<JsonArray> Batches { get; } = [];
        public bool LoseNextAnswer { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Method == HttpMethod.Get)
            {
                var sheets = new JsonArray(State.Select(t => (JsonNode)new JsonObject
                {
                    ["properties"] = new JsonObject { ["sheetId"] = t.Id, ["title"] = t.Title },
                    ["developerMetadata"] = t.Tag is null ? new JsonArray()
                        : new JsonArray(new JsonObject { ["metadataKey"] = GoogleSheets.TabTag, ["metadataValue"] = t.Tag }),
                }).ToArray());
                return Answer(new JsonObject { ["sheets"] = sheets });
            }
            var requests = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!["requests"]!.AsArray();
            Batches.Add(requests);
            foreach (var r in requests)
            {
                if (r!["addSheet"]?["properties"] is { } p) State.Add(Tab((long)p["sheetId"]!, (string)p["title"]!));
                if (r["createDeveloperMetadata"]?["developerMetadata"] is { } m)
                {
                    var at = State.FindIndex(t => t.Id == (long)m["location"]!["sheetId"]!);
                    State[at] = State[at] with { Tag = (string)m["metadataValue"]! };
                }
            }
            if (LoseNextAnswer) { LoseNextAnswer = false; throw new HttpRequestException("the answer was lost"); }
            return Answer(new JsonObject());
        }

        private static HttpResponseMessage Answer(JsonNode body) =>
            new(System.Net.HttpStatusCode.OK) { Content = new StringContent(body.ToJsonString()) };
    }

    [Fact]
    public void The_browser_is_told_of_a_refusal_and_never_told_connected_before_the_app_knows()
    {
        var refused = GoogleSheets.CallbackPage(new Dictionary<string, string> { ["state"] = "s", ["error"] = "access_denied<b>" });
        var received = GoogleSheets.CallbackPage(new Dictionary<string, string> { ["state"] = "s", ["code"] = "c" });

        Assert.Contains("did not connect", refused);
        Assert.Contains("access_denied&lt;b&gt;", refused);
        Assert.DoesNotContain("connected.", received);
        Assert.Contains("Received", received);
    }

    [Fact]
    public void The_redirect_query_is_decoded()
    {
        var q = GoogleSheets.ParseQuery("state=a-b_c&code=4%2F0Ab&scope=https%3A%2F%2Fwww.googleapis.com%2Fauth%2Fdrive.file");

        Assert.Equal("4/0Ab", q["code"]);
        Assert.Equal(GoogleSheets.Scope, q["scope"]);
        Assert.Equal("a-b_c", q["state"]);
    }

    [Fact]
    public void A_desktop_client_json_is_read_and_a_web_one_is_refused()
    {
        var client = GoogleClient.FromJson("""{"installed":{"client_id":"id.apps.googleusercontent.com","client_secret":"s"}}""");

        Assert.Equal(new GoogleClient("id.apps.googleusercontent.com", "s"), client);
        Assert.Throws<FormatException>(() => GoogleClient.FromJson("""{"web":{"client_id":"x","client_secret":"y"}}"""));
    }
}
