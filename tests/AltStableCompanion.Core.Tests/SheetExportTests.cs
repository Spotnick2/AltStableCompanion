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

    // A whole file: the addon always writes both tables.
    private static string Db(params string[] records) => "AltStableDB = {\n" + string.Concat(records) + "}\nAltStableConfig = {\n}\n";

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
        var table = SheetExport.BuildCharacters([Char(("name", "K"), ("class", "SHAMAN"), ("money", 102690))], 0);
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

    [Fact]
    public void A_copy_the_player_made_of_our_tab_is_left_alone()
    {
        var (ids, _) = GoogleSheets.PlanTabs([Table("Gear")],
            [Tab(5, "Gear", "Gear"), Tab(6, "Copy of Gear", "Gear")], () => 77);

        Assert.Equal(5, ids["Gear"]);
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
              {"properties":{"sheetId":1,"title":"Gear"},"developerMetadata":[{"metadataKey":"other.app","metadataValue":"Gear"},{"metadataKey":"altstable.tab","metadataValue":"Gear"}]},
              {"properties":{"sheetId":2,"title":"Skills"},"developerMetadata":[{"metadataKey":"other.app","metadataValue":"Skills"}]},
              {"properties":{"sheetId":3,"title":"Notes"}}]}
            """)!;

        Assert.Equal([Tab(1, "Gear", "Gear"), Tab(2, "Skills"), Tab(3, "Notes")], GoogleSheets.Tabs(listing));
    }

    [Fact]
    public async Task A_missing_tab_is_added_and_tagged_in_the_same_batch_that_fills_it()
    {
        // The player has a "Skills" of their own: ours comes back as "Skills (AltStable)", tagged "Skills".
        var google = new FakeGoogle([Tab(1, "Characters", "Characters"), Tab(2, "Skills")]);

        var added = await new GoogleSheets(new HttpClient(google), new GoogleClient("id", "secret"))
            .WriteAsync("token", "sheet", [Table("Characters"), Table("Skills")], CancellationToken.None);

        Assert.Equal(["Skills (AltStable)"], added);
        var requests = Assert.Single(google.Batches);
        var newId = (long)requests[0]!["addSheet"]!["properties"]!["sheetId"]!;
        var tag = requests[1]!["createDeveloperMetadata"]!["developerMetadata"]!;
        Assert.Equal(GoogleSheets.TabTag, (string)tag["metadataKey"]!);
        Assert.Equal("Skills", (string)tag["metadataValue"]!);
        Assert.Equal(newId, (long)tag["location"]!["sheetId"]!);
        Assert.Contains(requests, r => r!["updateCells"]?["range"]?["sheetId"] is JsonNode id && (long)id == newId);
        Assert.Contains(requests, r => r!["updateCells"]?["range"]?["sheetId"] is JsonNode id && (long)id == 1);
        Assert.DoesNotContain(requests, r => r!["updateCells"]?["range"]?["sheetId"] is JsonNode id && (long)id == 2);
    }

    [Fact]
    public async Task A_batch_Google_applied_but_whose_answer_was_lost_is_not_redone()
    {
        var google = new FakeGoogle([Tab(1, "Characters", "Characters")]) { LoseNextAnswer = true };
        var sheets = new GoogleSheets(new HttpClient(google), new GoogleClient("id", "secret"));
        SheetTable[] tables = [Table("Characters"), Table("Skills")];

        await Assert.ThrowsAsync<HttpRequestException>(() => sheets.WriteAsync("token", "sheet", tables, CancellationToken.None));
        var added = await sheets.WriteAsync("token", "sheet", tables, CancellationToken.None);

        Assert.Empty(added);
        Assert.Equal(2, google.State.Count);    // Characters and the one Skills: no "Skills (AltStable)"
    }

    /// <summary>
    /// The Sheets API as far as the export uses it: a listing of tabs with our tags, and a
    /// batch that applies addSheet and createDeveloperMetadata to that listing.
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
