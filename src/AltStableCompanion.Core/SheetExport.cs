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

/// <summary>
/// What the export read: one record per character, and the files it could not read.
/// <see cref="NoAccounts"/>: the folder holds no account that uses AltStable - a wrong path, not
/// an empty roster.
/// </summary>
public sealed record ExportRoster(IReadOnlyList<Dictionary<object, object?>> Characters, IReadOnlyList<string> Skipped,
    bool NoAccounts = false)
{
    /// <summary>
    /// Whether this may replace what the sheet has: every account read, and at least one. An
    /// account whose roster is empty still counts - its export is an empty one.
    /// </summary>
    public bool Complete => Skipped.Count == 0 && !NoAccounts;
}

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
    /// The roster of every account that uses AltStable (<see cref="Leftovers.AccountFiles"/>):
    /// one whose <c>AltStable.lua</c> is missing - only its <c>.bak</c> while the game replaces
    /// it - is named in <see cref="ExportRoster.Skipped"/>, not left out. A folder with no such
    /// account at all (a typo, or <c>WTF</c> instead of <c>WTF\Account</c>) is
    /// <see cref="ExportRoster.NoAccounts"/>: it must not empty the sheet (Codex on #83).
    /// </summary>
    public static ExportRoster Load(string accountsDir)
    {
        var files = Leftovers.AccountFiles(accountsDir).ToList();
        return files.Count == 0 ? new ExportRoster([], [], NoAccounts: true) : Load(files);
    }

    /// <summary>
    /// These files' <c>AltStableDB</c>, one record per GUID: the newer <c>lastUpdate</c> wins when
    /// two accounts hold the same character, and one hidden in any account is left out. A file
    /// that cannot be read (missing, or WoW is writing it), or that lacks the roster or the
    /// config table, is named in <see cref="ExportRoster.Skipped"/>; the export must not be
    /// written then. It would drop that account's rows, and could show a character only that
    /// account hides. The addon always writes both tables (<c>AltStableConfig or {}</c>), so a
    /// file without one is not a whole one.
    /// </summary>
    internal static ExportRoster Load(IEnumerable<string> paths)
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
            if (roster.Problem is not null
                || LuaTableScanner.ReadGlobal(text, SavedVariablesReader.RosterGlobal, out _) is not Dictionary<object, object?> db
                || LuaTableScanner.ReadGlobal(text, SavedVariablesReader.ConfigGlobal, out _) is not Dictionary<object, object?>)
            {
                skipped.Add(path);
                continue;
            }
            hidden.UnionWith(roster.Hidden);
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

    /// <summary>The three tabs, in the order they appear, as of <paramref name="now"/> (epoch seconds).</summary>
    public static IReadOnlyList<SheetTable> Build(IReadOnlyList<Dictionary<object, object?>> chars, long now) =>
        [BuildCharacters(chars, now), BuildGear(chars), BuildSkills(chars)];

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

    internal static SheetTable BuildCharacters(IReadOnlyList<Dictionary<object, object?>> chars, long now)
    {
        var header = Header("Name", "Realm", "Class", "Race", "Faction", "Level", "iLvl", "Rested", "Gold", "Guild", "Last seen");
        var rows = new List<IReadOnlyList<SheetCell>>
        {
            header.Select(h => (string)h.Value! == "Rested"
                ? h with { Note = "Rested XP as a % of the next level, carried forward to the export as AltStable's own column does" }
                : h).ToList(),
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
                RestedPercent(c, now) is double rested ? new SheetCell(rested, NumberFormat: "0\"%\"") : SheetCell.Empty,
                money is double m ? new SheetCell(m / 10000.0, NumberFormat: "#,##0.00\" g\"") : SheetCell.Empty,
                Text(Str(c, "guild")),
                seen is double s and > 0 ? new SheetCell(SheetDate(s), NumberFormat: "yyyy-mm-dd hh:mm") : SheetCell.Empty,
            ]);
        }
        return new SheetTable(Characters, rows);
    }

    // The addon's ComputeLiveRestedPercent (RowRenderer.lua) for a character not logged in: the
    // snapshot, plus what accrues since - 5% of a level per 8 h in a rested area, per 32 h
    // outside, 1.2x with Well Rested - up to the cap (150%, or 180% with Well Rested). No
    // snapshot is no value, not 0. At the level cap the scan stores xpMax = 0: nothing accrues.
    internal static double? RestedPercent(Dictionary<object, object?> c, long now)
    {
        if (Num(c, "restPercent") is not double stored) return null;
        if (Num(c, "xpMax") is double max && max <= 0) return 0;
        var wellRested = Num(c, "wellRested") == 1 || stored > 150;
        var inRestedArea = c.TryGetValue("restedArea", out var area) && area is true or "true";
        var perHour = (inRestedArea ? 0.625 : 0.15625) * (wellRested ? 1.2 : 1);
        var since = Num(c, "restTimestamp") ?? Num(c, "lastUpdate") ?? 0;
        var live = stored + Math.Max(0, now - since) / 3600.0 * perHour;
        var cap = wellRested ? Math.Max(180, stored) : 150;
        return Math.Floor(Math.Min(live, cap) + 0.5);
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
