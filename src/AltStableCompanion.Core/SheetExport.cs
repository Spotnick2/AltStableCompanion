using System.Globalization;

namespace AltStableCompanion.Core;

/// <summary>One cell of an exported tab: a string or a number, and how it looks. Colours are "RRGGBB".</summary>
public sealed record SheetCell(object? Value, string? Fill = null, string? Color = null, bool Bold = false,
    string? NumberFormat = null, string? Note = null, bool Vertical = false)
{
    public static readonly SheetCell Empty = new((object?)null);
}

/// <summary>One tab the app owns: rewritten whole on every export. Row 0 is the header.</summary>
public sealed record SheetTable(string Title, IReadOnlyList<IReadOnlyList<SheetCell>> Rows, int FrozenColumns = 1)
{
    public int Columns => Rows.Count == 0 ? 0 : Rows.Max(r => r.Count);
}

/// <summary>What the export read: one record per character, and the files it could not read.</summary>
public sealed record ExportRoster(IReadOnlyList<Dictionary<object, object?>> Characters, IReadOnlyList<string> Skipped);

/// <summary>
/// The Google Sheet export (#82): every account's <c>AltStableDB</c> as the Characters, Gear and
/// Skills tabs. Pure - no Google here; <see cref="GoogleSheets"/> writes what this builds.
/// </summary>
public static class SheetExport
{
    public const string Characters = "Characters";
    public const string Gear = "Gear";
    public const string Skills = "Skills";

    // The paper doll's order (the addon's AltStable.GEAR_SLOTS).
    internal static readonly (string Key, string Label)[] GearSlots =
    [
        ("head", "Head"), ("neck", "Neck"), ("shoulder", "Shoulder"), ("back", "Back"), ("chest", "Chest"),
        ("wrist", "Wrist"), ("hands", "Hands"), ("waist", "Waist"), ("legs", "Legs"), ("feet", "Feet"),
        ("ring1", "Ring 1"), ("ring2", "Ring 2"), ("trinket1", "Trinket 1"), ("trinket2", "Trinket 2"),
        ("mainhand", "Main Hand"), ("offhand", "Off Hand"), ("ranged", "Ranged"),
    ];

    // Label, the skill's field, its cap's field. Primary professions are prof_<Name>/profmax_<Name>
    // (each its own field since AltStable#177); the secondary ones and riding have their own.
    internal static readonly (string Label, string Field, string MaxField)[] SkillColumns =
    [
        ("Alchemy", "prof_Alchemy", "profmax_Alchemy"),
        ("Blacksmithing", "prof_Blacksmithing", "profmax_Blacksmithing"),
        ("Enchanting", "prof_Enchanting", "profmax_Enchanting"),
        ("Engineering", "prof_Engineering", "profmax_Engineering"),
        ("Leatherworking", "prof_Leatherworking", "profmax_Leatherworking"),
        ("Tailoring", "prof_Tailoring", "profmax_Tailoring"),
        ("Herbalism", "prof_Herbalism", "profmax_Herbalism"),
        ("Mining", "prof_Mining", "profmax_Mining"),
        ("Skinning", "prof_Skinning", "profmax_Skinning"),
        ("Cooking", "cooking", "cookingMax"),
        ("Fishing", "fishing", "fishingMax"),
        ("First Aid", "firstAid", "firstAidMax"),
        ("Riding", "riding", "ridingMax"),
    ];

    // RAID_CLASS_COLORS, as the game draws class names.
    private static readonly Dictionary<string, string> ClassColors = new(StringComparer.Ordinal)
    {
        ["WARRIOR"] = "C69B6D", ["PALADIN"] = "F48CBA", ["HUNTER"] = "AAD372", ["ROGUE"] = "FFF468",
        ["PRIEST"] = "FFFFFF", ["SHAMAN"] = "0070DD", ["MAGE"] = "3FC7EB", ["WARLOCK"] = "8788EE",
        ["DRUID"] = "FF7C0A", ["DEATHKNIGHT"] = "C41E3A",
    };

    // Item quality, as the game draws item names: poor .. heirloom.
    private static readonly string[] QualityColors = ["9D9D9D", "FFFFFF", "1EFF00", "0070DD", "A335EE", "FF8000", "E6CC80", "00CCFF"];

    private const string HeaderFill = "D9D9D9";
    private const string ItemFill = "1F1F1F";     // item names in their game colours, on a tooltip's dark
    private const string CappedFill = "FFF200";   // a skill at its current cap: time to train the next rank
    private const string HordeFill = "B30000";
    private const string AllianceFill = "0047AB";

    /// <summary>
    /// Every file's <c>AltStableDB</c>, one record per GUID: the newer <c>lastUpdate</c> wins when
    /// two accounts hold the same character, and one hidden in any account is left out. A file
    /// that cannot be read (WoW is writing it) is named in <see cref="ExportRoster.Skipped"/>;
    /// the export must not be written then, since it would drop that account's rows.
    /// </summary>
    public static ExportRoster Load(IEnumerable<string> paths)
    {
        var newest = new Dictionary<string, Dictionary<object, object?>>(StringComparer.Ordinal);
        var hidden = new HashSet<string>(StringComparer.Ordinal);
        var skipped = new List<string>();
        foreach (var path in paths)
        {
            string text;
            try { text = SavedVariablesReader.ReadShared(path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { skipped.Add(path); continue; }
            var roster = SavedVariablesReader.ParseRoster(text, path);
            if (roster.Problem is not null) { skipped.Add(path); continue; }
            hidden.UnionWith(roster.Hidden);
            if (LuaTableScanner.ReadGlobal(text, SavedVariablesReader.RosterGlobal, out _) is not Dictionary<object, object?> db) continue;
            foreach (var (key, entry) in db)
            {
                if (key is not string guid || entry is not Dictionary<object, object?> e) continue;
                if (string.IsNullOrWhiteSpace(Str(e, "name"))) continue;
                if (newest.TryGetValue(guid, out var had) && (Num(had, "lastUpdate") ?? 0) >= (Num(e, "lastUpdate") ?? 0)) continue;
                newest[guid] = e;
            }
        }
        var chars = newest.Where(kv => !hidden.Contains(kv.Key)).Select(kv => kv.Value).ToList();
        chars.Sort(Order);
        return new ExportRoster(chars, skipped);
    }

    /// <summary>The three tabs, in the order they appear.</summary>
    public static IReadOnlyList<SheetTable> Build(IReadOnlyList<Dictionary<object, object?>> chars) =>
        [BuildCharacters(chars), BuildGear(chars), BuildSkills(chars)];

    // Horde, then Alliance, then anything else; within a side, highest level, then ilvl, then name.
    internal static int Order(Dictionary<object, object?> a, Dictionary<object, object?> b)
    {
        static int Side(Dictionary<object, object?> c) => Str(c, "faction") switch { "Horde" => 0, "Alliance" => 1, _ => 2 };
        var c = Side(a).CompareTo(Side(b));
        if (c != 0) return c;
        c = (Num(b, "level") ?? 0).CompareTo(Num(a, "level") ?? 0);
        if (c != 0) return c;
        c = (Num(b, "ilvl") ?? 0).CompareTo(Num(a, "ilvl") ?? 0);
        return c != 0 ? c : string.CompareOrdinal(Str(a, "name"), Str(b, "name"));
    }

    internal static SheetTable BuildCharacters(IReadOnlyList<Dictionary<object, object?>> chars)
    {
        var rows = new List<IReadOnlyList<SheetCell>>
        {
            Header("Name", "Realm", "Class", "Race", "Faction", "Level", "iLvl", "Gold", "Guild", "Last seen"),
        };
        foreach (var c in chars)
        {
            var faction = Str(c, "faction");
            var money = Num(c, "money");
            var seen = Num(c, "lastUpdate");
            rows.Add(
            [
                NameCell(c),
                Text(Str(c, "realm")),
                Text(Title(Str(c, "class"))),
                Text(Str(c, "raceName") ?? Str(c, "race")),
                faction is "Horde" or "Alliance"
                    ? new SheetCell(faction, faction == "Horde" ? HordeFill : AllianceFill, "FFFFFF", Bold: true)
                    : Text(faction),
                Number(Num(c, "level")),
                Num(c, "ilvl") is double ilvl ? new SheetCell(Math.Round(ilvl, 1), NumberFormat: "0.0") : SheetCell.Empty,
                money is double m ? new SheetCell(m / 10000.0, NumberFormat: "#,##0.00\" g\"") : SheetCell.Empty,
                Text(Str(c, "guild")),
                seen is double s and > 0 ? new SheetCell(SheetDate(s), NumberFormat: "yyyy-mm-dd hh:mm") : SheetCell.Empty,
            ]);
        }
        return new SheetTable(Characters, rows);
    }

    internal static SheetTable BuildGear(IReadOnlyList<Dictionary<object, object?>> chars)
    {
        var rows = new List<IReadOnlyList<SheetCell>>
        {
            Header(["Name", "Level", "iLvl", .. GearSlots.Select(s => s.Label)]),
        };
        foreach (var c in chars)
        {
            var row = new List<SheetCell>
            {
                NameCell(c),
                Number(Num(c, "level")),
                Num(c, "ilvl") is double ilvl ? new SheetCell(Math.Round(ilvl, 1), NumberFormat: "0.0") : SheetCell.Empty,
            };
            foreach (var (key, _) in GearSlots) row.Add(ItemCell(c, key));
            rows.Add(row);
        }
        return new SheetTable(Gear, rows);
    }

    internal static SheetTable BuildSkills(IReadOnlyList<Dictionary<object, object?>> chars)
    {
        var rows = new List<IReadOnlyList<SheetCell>>
        {
            Header(["Name", "Level", .. SkillColumns.Select(s => s.Label)], verticalFrom: 2),
        };
        foreach (var c in chars)
        {
            var row = new List<SheetCell> { NameCell(c), Number(Num(c, "level")) };
            foreach (var (_, field, maxField) in SkillColumns)
            {
                // 0 is "not learned": an empty cell, as in the hand-kept sheet.
                if (Num(c, field) is not double v || v <= 0) { row.Add(SheetCell.Empty); continue; }
                var max = Num(c, maxField);
                row.Add(new SheetCell(v, Fill: max is double mx && mx > 0 && v >= mx ? CappedFill : null,
                    Note: max is double cap && cap > 0 ? $"{v.ToString(CultureInfo.InvariantCulture)} / {cap.ToString(CultureInfo.InvariantCulture)}" : null));
            }
            rows.Add(row);
        }
        return new SheetTable(Skills, rows);
    }

    // The item's name in its quality's colour; its ilvl and enchant as the cell's note. A slot
    // whose name the client had not cached at scan time still shows that something is there.
    private static SheetCell ItemCell(Dictionary<object, object?> c, string key)
    {
        var id = Num(c, "gearid_" + key) ?? 0;
        var name = Str(c, "gearname_" + key);
        if (string.IsNullOrEmpty(name))
        {
            if (id <= 0) return SheetCell.Empty;
            name = "item " + id.ToString(CultureInfo.InvariantCulture);
        }
        var q = (int)(Num(c, "gearq_" + key) ?? 1);
        var color = q >= 0 && q < QualityColors.Length ? QualityColors[q] : QualityColors[1];
        var note = new List<string>();
        if (Num(c, "gear_" + key) is double ilvl and > 0) note.Add("Item level " + ilvl.ToString(CultureInfo.InvariantCulture));
        if (Str(c, "gearench_" + key) is { Length: > 0 } ench) note.Add("Enchanted: " + ench);
        return new SheetCell(name, ItemFill, color, Note: note.Count > 0 ? string.Join('\n', note) : null);
    }

    private static SheetCell NameCell(Dictionary<object, object?> c) =>
        new(Str(c, "name"), Str(c, "class") is { } cls && ClassColors.TryGetValue(cls, out var fill) ? fill : null, Bold: true);

    private static IReadOnlyList<SheetCell> Header(params string[] labels) => Header(labels, int.MaxValue);

    private static IReadOnlyList<SheetCell> Header(string[] labels, int verticalFrom) =>
        labels.Select((l, i) => new SheetCell(l, HeaderFill, Bold: true, Vertical: i >= verticalFrom)).ToList();

    private static SheetCell Text(string? s) => string.IsNullOrEmpty(s) ? SheetCell.Empty : new SheetCell(s);

    private static SheetCell Number(double? d) => d is double v ? new SheetCell(v) : SheetCell.Empty;

    // "DRUID" -> "Druid", "DEATHKNIGHT" -> "Deathknight": the class token, readable.
    private static string? Title(string? token) =>
        string.IsNullOrEmpty(token) ? token : token[..1].ToUpperInvariant() + token[1..].ToLowerInvariant();

    /// <summary>
    /// Epoch seconds as a Sheets date serial (days since 1899-12-30), in this PC's local time:
    /// a serial has no time zone, so it shows exactly the wall-clock time it holds.
    /// </summary>
    internal static double SheetDate(double epochSeconds) =>
        DateTimeOffset.FromUnixTimeSeconds((long)epochSeconds).ToLocalTime().DateTime.ToOADate();

    private static string? Str(Dictionary<object, object?> e, string key) =>
        e.TryGetValue(key, out var v) ? v as string : null;

    private static double? Num(Dictionary<object, object?> e, string key) =>
        e.TryGetValue(key, out var v) && v is double d ? d : null;
}
