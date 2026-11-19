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

    private static string Record(string guid, params (string Key, object Value)[] fields) =>
        $"[\"{guid}\"] = {{\n" + string.Concat(fields.Select(f => f.Value is string s
            ? $"[\"{f.Key}\"] = \"{s}\",\n"
            : $"[\"{f.Key}\"] = {Convert.ToString(f.Value, CultureInfo.InvariantCulture)},\n")) + "},\n";

    private static string Db(params string[] records) => "AltStableDB = {\n" + string.Concat(records) + "}\n";

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
        var b = Store("B", "AltStableConfig = {\n[\"hiddenCharacters\"] = {\n[\"Player-1-02\"] = true,\n},\n}\n");

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

    [Fact]
    public void A_skill_at_its_cap_is_highlighted_one_below_is_not_and_zero_is_empty()
    {
        var table = SheetExport.BuildSkills([Char(("name", "K"), ("level", 30),
            ("prof_Enchanting", 150), ("profmax_Enchanting", 150),
            ("prof_Tailoring", 149), ("profmax_Tailoring", 150),
            ("fishing", 0), ("fishingMax", 75))]);

        var header = table.Rows[0].Select(c => c.Value).ToList();
        SheetCell Cell(string label) => table.Rows[1][header.IndexOf(label)];
        Assert.Equal(150.0, Cell("Enchanting").Value);
        Assert.NotNull(Cell("Enchanting").Fill);
        Assert.Null(Cell("Tailoring").Fill);
        Assert.Equal("149 / 150", Cell("Tailoring").Note);
        Assert.Null(Cell("Fishing").Value);
        Assert.True(table.Rows[0][header.IndexOf("Alchemy")].Vertical);
        Assert.False(table.Rows[0][0].Vertical);
    }

    [Fact]
    public void An_item_shows_its_name_in_its_quality_colour_with_ilvl_and_enchant_as_its_note()
    {
        var table = SheetExport.BuildGear([Char(("name", "K"),
            ("gearname_chest", "Civinad Robes"), ("gearq_chest", 3), ("gear_chest", 31), ("gearid_chest", 9623),
            ("gearench_chest", "Stamina +2"),
            ("gearname_feet", ""), ("gearid_feet", 9454), ("gearq_feet", 2),
            ("gearname_head", ""), ("gearid_head", 0))]);

        var header = table.Rows[0].Select(c => c.Value).ToList();
        SheetCell Cell(string label) => table.Rows[1][header.IndexOf(label)];
        Assert.Equal("Civinad Robes", Cell("Chest").Value);
        Assert.Equal("0070DD", Cell("Chest").Color);
        Assert.Equal("Item level 31\nEnchanted: Stamina +2", Cell("Chest").Note);
        Assert.Equal("item 9454", Cell("Feet").Value);       // worn, its name not cached at scan time
        Assert.Equal("1EFF00", Cell("Feet").Color);
        Assert.Null(Cell("Head").Value);                     // nothing worn
    }

    [Fact]
    public void Gold_is_copper_over_ten_thousand_and_the_name_takes_the_class_colour()
    {
        var table = SheetExport.BuildCharacters([Char(("name", "K"), ("class", "SHAMAN"), ("money", 102690))]);
        var header = table.Rows[0].Select(c => c.Value).ToList();

        Assert.Equal(10.269, (double)table.Rows[1][header.IndexOf("Gold")].Value!, 9);
        Assert.Equal("0070DD", table.Rows[1][0].Fill);
        Assert.Equal("Shaman", table.Rows[1][header.IndexOf("Class")].Value);
    }

    [Fact]
    public void A_sheet_date_is_days_since_1899_12_30_in_local_time()
    {
        var epoch = 1795044105L;
        var local = DateTimeOffset.FromUnixTimeSeconds(epoch).ToLocalTime().DateTime;

        Assert.Equal(local, DateTime.FromOADate(SheetExport.SheetDate(epoch)), TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void The_write_clears_the_whole_tab_sizes_its_grid_and_freezes_the_header()
    {
        var table = new SheetTable("Skills", [[new SheetCell("Name"), new SheetCell("Level")], [new SheetCell("K", "FF7C0A"), new SheetCell(30.0)]]);

        var requests = GoogleSheets.BuildWriteRequests([table], new Dictionary<string, long> { ["Skills"] = 7 });

        var grid = requests[0]!["updateSheetProperties"]!["properties"]!["gridProperties"]!;
        Assert.Equal(3, (int)grid["rowCount"]!);          // two rows, one spare: the header is never every row
        Assert.Equal(2, (int)grid["columnCount"]!);
        Assert.Equal(1, (int)grid["frozenRowCount"]!);
        var cells = requests[1]!["updateCells"]!;
        // The range is the whole tab, nothing narrower: a cell the table no longer has is cleared.
        Assert.Equal(["sheetId"], cells["range"]!.AsObject().Select(p => p.Key));
        Assert.Equal(7, (long)cells["range"]!["sheetId"]!);
        var name = cells["rows"]![1]!["values"]![0]!;
        Assert.Equal("K", (string)name["userEnteredValue"]!["stringValue"]!);
        Assert.Equal(1.0, (double)name["userEnteredFormat"]!["backgroundColor"]!["red"]!);
        Assert.Equal(124 / 255.0, (double)name["userEnteredFormat"]!["backgroundColor"]!["green"]!, 9);
        Assert.Equal(30.0, (double)cells["rows"]![1]!["values"]![1]!["userEnteredValue"]!["numberValue"]!);
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
