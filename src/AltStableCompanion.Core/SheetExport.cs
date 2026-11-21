using System.Globalization;

namespace AltStableCompanion.Core;

/// <summary>
/// One cell of an exported tab: a string, a number or a <see cref="SheetImage"/>, and how it
/// looks. Colours are "RRGGBB". <see cref="Link"/>: the cell's text links there (an item's
/// Wowhead page), the value staying what it is. <see cref="BorderTop"/>: a fine line above it.
/// </summary>
public sealed record SheetCell(object? Value, string? Fill = null, string? Color = null, bool Bold = false,
    string? NumberFormat = null, string? Note = null, bool Vertical = false, bool Center = false, string? Link = null,
    int? FontSize = null, bool Wrap = false, string? BorderTop = null, bool Left = false)
{
    public static readonly SheetCell Empty = new((object?)null);
}

/// <summary>A cell's picture, by its address: an <c>=IMAGE()</c> formula, so Google fetches it, not the app.</summary>
public sealed record SheetImage(string Url);

/// <summary>
/// The pictures, with portraits from the armory on: the HD or SD model, and the characters whose
/// render the app holds (<see cref="CutoutFolder.ArmoryRenders"/>). The others get the armory's
/// silhouette: an <c>=IMAGE()</c> of a render that is not there is no error, just nothing
/// (measured 2026-11-19), so the sheet cannot fall back on its own.
/// </summary>
public sealed record AvatarSource(bool Sd, IReadOnlySet<string> Drawn);

/// <summary>Cells merged into one: from (<see cref="Row"/>, <see cref="Column"/>), that many of each.</summary>
public sealed record SheetMerge(int Row, int Column, int Rows, int Columns);

/// <summary>The tab's filter: rows <see cref="FromRow"/> (its header) to <see cref="ToRow"/>, exclusive, every column.</summary>
public sealed record SheetFilter(int FromRow, int ToRow);

/// <summary>
/// One tab the app owns: rewritten whole on every export. <see cref="RowHeights"/> and
/// <see cref="ColumnWidths"/> are in pixels, one per row and per column; null is the default
/// height, and a column fitted to its contents.
/// </summary>
public sealed record SheetTable(string Title, IReadOnlyList<IReadOnlyList<SheetCell>> Rows, int FrozenColumns = 1,
    int FrozenRows = 1, IReadOnlyList<int?>? RowHeights = null, IReadOnlyList<int?>? ColumnWidths = null,
    IReadOnlyList<SheetMerge>? Merges = null, SheetFilter? Filter = null)
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

/// <summary>A reputation AltStable tracks (the addon's <c>Reputations.lua</c>).</summary>
public sealed record SheetReputation(int Id, string Label);

/// <summary>
/// The sheet's colours, one per theme (the owner's choice: dark or light, 2026-11-20). Dark is the
/// redesign's (GPT-Astra's handoff, 2026-11-20: AltStable's own charcoal and gold); light is the
/// same roles on paper. Class colours are tinted toward <see cref="ClassMixWith"/> to read on the
/// rows; item quality and standings have their own readable sets.
/// </summary>
public sealed record SheetPalette(string Name,
    string Masthead, string RowA, string RowB, string Band, string Header,
    string Text, string Secondary, string Gold, string Cyan, string Horde, string Alliance, string Divider,
    string CappedText, string CappedFill, double ClassMix, string ClassMixWith,
    IReadOnlyList<string> Quality, IReadOnlyList<(string Text, string? Fill)> Standings)
{
    public const string DarkName = "dark";
    public const string LightName = "light";

    public static readonly SheetPalette Dark = new(DarkName,
        "141414", "1A1A1A", "222222", "292929", "353535",
        "F0EFED", "BDBAB4", "EAC675", "8FD1ED", "E8806E", "7FA7E6", "4A4A4A",
        "EAC675", "3B3115", 0.2, "FFFFFF",
        ["A8A8A8", "F0EFED", "7DDC6E", "6AA8F0", "C08AF5", "FFA54D", "E6CC80", "66D9F2"],
        [("", null), ("E07A6A", null), ("E07A6A", null), ("E8A060", null), ("BDBAB4", null),
         ("8FD18F", null), ("8FE3B0", "1D3A30"), ("8FE3D6", "1D3A3A"), ("8FD1ED", "1D3245")]);

    public static readonly SheetPalette Light = new(LightName,
        "F4F2EE", "FFFFFF", "F7F6F3", "ECE9E3", "E2DED7",
        "1E1E1E", "5E5A54", "9A6B12", "1F78A8", "B23A2E", "2F5EA8", "9C978F",
        "8A5A00", "FFF1C7", 0.45, "000000",
        ["7A7A7A", "1E1E1E", "1A8C1A", "1F5FB8", "8A35C9", "C46A00", "9C7A2E", "0A89A8"],
        [("", null), ("B23A2E", null), ("B23A2E", null), ("B86A1E", null), ("5E5A54", null),
         ("2E8B3A", null), ("1F7A4A", "E3F4EA"), ("1A7A70", "E0F3F1"), ("1F6E99", "E2F0F8")]);

    /// <summary>A theme by name; anything else is dark.</summary>
    public static SheetPalette Of(string? name) => name == LightName ? Light : Dark;

    /// <summary>A class colour, made readable on this theme's rows.</summary>
    public string Tint(string rrggbb) => Mix(rrggbb, ClassMixWith, ClassMix);

    internal static string Mix(string a, string b, double share)
    {
        int Part(string c, int at) => int.Parse(c.AsSpan(at, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        var parts = new[] { 0, 2, 4 }.Select(i => (int)Math.Round(Part(a, i) * (1 - share) + Part(b, i) * share));
        return string.Concat(parts.Select(v => v.ToString("X2", CultureInfo.InvariantCulture)));
    }
}

/// <summary>
/// The Google Sheet export (#82): every account's <c>AltStableDB</c> as the Characters, Skills,
/// Reputations and Gear tabs. Pure - no Google here; <see cref="GoogleSheets"/> writes what this builds.
/// <para>
/// The layout (GPT-Astra's handoff, the owner's, 2026-11-20): every tab is one table the player
/// can filter and sort. Rows 1-2 a masthead (the section's icon from AltStable's sidebar, the
/// view's title, the last export and a few figures), row 3 short group labels, row 4 the column
/// names with the filter, then a row per character in the roster's order - Horde, Alliance,
/// other - a fine line where the faction changes. Columns A-E are the same on every tab:
/// Portrait, Character, Account, Class, Faction. Frozen: the four rows and A-C. The colours are
/// a <see cref="SheetPalette"/>, dark or light.
/// </para>
/// </summary>
public static class SheetExport
{
    public const string Characters = "Characters";
    public const string Skills = "Skills";
    public const string Reputations = "Reputations";
    public const string Gear = "Gear";

    /// <summary>An item's Wowhead Forever page (the wowhead-forever skill's page prefix): its tooltip, a click away.</summary>
    public const string WowheadItem = "https://www.wowhead.com/forever/item=";

    /// <summary>
    /// The commit the masthead's icons are read from (docs/sheet-icons): fixed, so a player's
    /// sheet never changes under them. Google fetches them, not the app.
    /// </summary>
    public const string IconCommit = "08a276a6e0d57de11283820a44f7eb03e7b45570";
    public const string IconBase = "https://raw.githubusercontent.com/Spotnick2/AltStableCompanion/" + IconCommit + "/docs/sheet-icons/";

    // The paper doll's order (the addon's AltStable.GEAR_SLOTS): armour and neck, rings and
    // trinkets, weapons - the Gear tab's groups.
    internal static readonly (string Key, string Label)[] GearSlots =
    [
        ("head", "Head"), ("neck", "Neck"), ("shoulder", "Shoulder"), ("back", "Back"), ("chest", "Chest"),
        ("wrist", "Wrist"), ("hands", "Hands"), ("waist", "Waist"), ("legs", "Legs"), ("feet", "Feet"),
        ("ring1", "Ring 1"), ("ring2", "Ring 2"), ("trinket1", "Trinket 1"), ("trinket2", "Trinket 2"),
        ("mainhand", "Main Hand"), ("offhand", "Off Hand"), ("ranged", "Ranged"),
    ];

    // The slots an enchant goes on, in the paper doll's order.
    internal static readonly string[] EnchantSlots = ["head", "shoulder", "back", "chest", "wrist", "hands", "legs", "feet", "mainhand", "offhand", "ranged"];

    // Label, the skill's field, its cap's field. Primary professions are prof_<Name>/profmax_<Name>
    // (each its own field since AltStable#177); the secondary ones and riding have their own.
    // Crafting, gathering, then the secondary skills and riding: the Skills tab's groups.
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

    /// <summary>The addon's <c>AltStable.REPUTATIONS</c> (Reputations.lua, AltStable 1.9.0), in its order.</summary>
    internal static readonly SheetReputation[] AllReputations =
    [
        new(72, "Stormwind"),
        new(47, "Ironforge"),
        new(69, "Darnassus"),
        new(54, "Gnomeregan Exiles"),
        new(2779, "High Order"),
        new(76, "Orgrimmar"),
        new(81, "Thunder Bluff"),
        new(68, "Undercity"),
        new(530, "Darkspear Trolls"),
        new(2778, "Windshapers"),
        new(529, "Argent Dawn"),
        new(609, "Cenarion Circle"),
        new(59, "Thorium Brotherhood"),
        new(576, "Timbermaw Hold"),
        new(749, "Hydraxian Waterlords"),
        new(270, "Zandalar Tribe"),
        new(910, "Brood of Nozdormu"),
        new(589, "Wintersaber Trainers"),
        new(21, "Booty Bay"),
        new(369, "Gadgetzan"),
        new(470, "Ratchet"),
        new(577, "Everlook"),
        new(87, "Bloodsail Buccaneers"),
        new(349, "Ravenholdt"),
        new(809, "Shen'dralar"),
        new(909, "Darkmoon Faire"),
        new(92, "Gelkis Clan Centaur"),
        new(93, "Magram Clan Centaur"),
        new(730, "Stormpike Guard"),
        new(729, "Frostwolf Clan"),
        new(890, "Silverwing Sentinels"),
        new(889, "Warsong Outriders"),
        new(509, "League of Arathor"),
        new(510, "The Defilers"),
        new(2799, "Theramore Expeditionary Force"),
        new(2798, "Darkspear Raiders"),
        new(2719, "Cenarion Scouts"),
        new(2740, "Kirin Tor"),
        new(2747, "Barkskin Burrow"),
        new(2758, "Nightclaw Druids"),
        new(2765, "Guardians of Hyjal"),
        new(2782, "Bolder'ok Clan"),
        new(2787, "Earthen Ring"),
        new(2819, "The Watchers"),
        new(2826, "Brotherhood of the Horse"),
        new(2827, "Powderfuse"),
        new(2586, "Azeroth Commerce Authority"),
        new(2587, "Durotar Supply and Logistics"),
    ];

    // A standing, rep_<id> = 1 Hated .. 8 Exalted.
    private static readonly string[] StandingNames = ["", "Hated", "Hostile", "Unfriendly", "Neutral", "Friendly", "Honored", "Revered", "Exalted"];

    // RAID_CLASS_COLORS, as the game draws class names; tinted per theme.
    private static readonly Dictionary<string, string> ClassColors = new(StringComparer.Ordinal)
    {
        ["WARRIOR"] = "C69B6D", ["PALADIN"] = "F48CBA", ["HUNTER"] = "AAD372", ["ROGUE"] = "FFF468",
        ["PRIEST"] = "FFFFFF", ["SHAMAN"] = "0070DD", ["MAGE"] = "3FC7EB", ["WARLOCK"] = "8788EE",
        ["DRUID"] = "FF7C0A", ["DEATHKNIGHT"] = "C41E3A",
    };

    // Item quality: poor .. heirloom, in words in the note, so the colour is extra.
    private static readonly string[] QualityNames = ["Poor", "Common", "Uncommon", "Rare", "Epic", "Legendary", "Artifact", "Heirloom"];

    // The sizes, in pixels and points (the handoff's 25/31/24/33, a 45 px reputation header).
    internal const int MastheadLabelHeight = 25, MastheadTitleHeight = 31, BandHeight = 24, HeaderHeight = 33, PairHeaderHeight = 45;
    internal const int RowHeight = 31;
    /// <summary>The portrait column: a 28-30 px picture in a 31 px row.</summary>
    public const int AvatarSize = 36;
    internal const int MastheadRows = 4;     // masthead (2), groups, column names
    private const int Lead = 5;              // Portrait, Character, Account, Class, Faction
    private const string CoinFormat = "0\"g \"00\"s \"00\"c\"";
    private const string DateFormat = "d mmm yyyy  hh:mm";

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
    /// <para>
    /// Each record gets <c>guid</c> (its key) and <c>accountNumber</c>: 1, 2, ... in the account
    /// folders' sorted order, of the account that SCANS the character - its copy says
    /// <c>scannedHere</c>, which AltStable never syncs (Core.lua, Provenance.LOCAL_ONLY) - not
    /// of whichever copy is newest: a synced copy can tie it, and a friend's character is on
    /// neither account. No such copy, no number. The folder names are Battle.net account
    /// identifiers, which never leave the PC (as in diagnostics); Codex on #85.
    /// </para>
    /// </summary>
    internal static ExportRoster Load(IEnumerable<string> paths)
    {
        var list = paths.ToList();
        var accounts = list.Select(AccountOf).Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase).ToList();
        var newest = new Dictionary<string, Dictionary<object, object?>>(StringComparer.Ordinal);
        var hidden = new HashSet<string>(StringComparer.Ordinal);
        var owner = new Dictionary<string, int>(StringComparer.Ordinal);    // the account that scans it
        var skipped = new List<string>();
        foreach (var path in list)
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
            var account = accounts.FindIndex(a => string.Equals(a, AccountOf(path), StringComparison.OrdinalIgnoreCase)) + 1;
            foreach (var (key, entry) in db)
            {
                if (key is not string guid || entry is not Dictionary<object, object?> e) continue;
                if (string.IsNullOrWhiteSpace(Str(e, "name"))) continue;
                e["guid"] = guid;       // the key is the character's identity, whatever the record says
                if (e.TryGetValue("scannedHere", out var here) && here is true or "true") owner[guid] = account;
                if (newest.TryGetValue(guid, out var had) && (Num(had, "lastUpdate") ?? 0) >= (Num(e, "lastUpdate") ?? 0)) continue;
                newest[guid] = e;
            }
        }
        foreach (var (guid, e) in newest)
        {
            if (owner.TryGetValue(guid, out var account)) e["accountNumber"] = (double)account;
            else e.Remove("accountNumber");
        }
        var chars = newest.Where(kv => !hidden.Contains(kv.Key)).Select(kv => kv.Value).ToList();
        chars.Sort(Order);
        return new ExportRoster(chars, skipped);
    }

    // ...\WTF\Account\<account>\SavedVariables\AltStable.lua -> <account>
    private static string AccountOf(string storePath) =>
        Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(storePath))) ?? storePath;

    /// <summary>
    /// The four tabs, in the order they appear, as of <paramref name="now"/> (epoch seconds).
    /// <paramref name="avatar"/>: the armory's pictures (portraits from the armory on); null
    /// leaves the Portrait column empty. <paramref name="palette"/>: dark unless named.
    /// </summary>
    public static IReadOnlyList<SheetTable> Build(IReadOnlyList<Dictionary<object, object?>> chars, long now,
        AvatarSource? avatar = null, SheetPalette? palette = null) =>
        [BuildCharacters(chars, now, avatar, palette), BuildSkills(chars, now, avatar, palette),
         BuildReputations(chars, now, avatar, palette), BuildGear(chars, now, avatar, palette)];

    // Horde, then Alliance, then anything else; within a side, highest level, then ilvl, then name.
    internal static int Order(Dictionary<object, object?> a, Dictionary<object, object?> b)
    {
        var c = Side(a).CompareTo(Side(b));
        if (c != 0) return c;
        c = (Num(b, "level") ?? 0).CompareTo(Num(a, "level") ?? 0);
        if (c != 0) return c;
        c = (Num(b, "ilvl") ?? 0).CompareTo(Num(a, "ilvl") ?? 0);
        return c != 0 ? c : string.CompareOrdinal(Str(a, "name"), Str(b, "name"));
    }

    private static int Side(Dictionary<object, object?> c) => Str(c, "faction") switch { "Horde" => 0, "Alliance" => 1, _ => 2 };

    private sealed record Group(string Label, int Columns);
    private sealed record Metric(string Label, SheetCell Value);

    /// <summary>The shared layout: a tab is its own columns after A-E, its metrics, its groups.</summary>
    private sealed class View(string title, string icon, long now, AvatarSource? avatar, SheetPalette p)
    {
        public SheetTable Build(IReadOnlyList<Dictionary<object, object?>> chars, IReadOnlyList<Metric> metrics,
            IReadOnlyList<Group> groups, IReadOnlyList<SheetCell> headers,
            Func<Dictionary<object, object?>, string, IEnumerable<SheetCell>> cells,
            int headerHeight = HeaderHeight, Func<IReadOnlyList<List<SheetCell>>>? footer = null)
        {
            // Text, not a date: only text runs on into the empty cells beside it (measured
            // 2026-11-20: a date there was cut at its column's edge).
            var allMetrics = new List<Metric> { new("LAST EXPORT", new SheetCell(LocalTime(now))) };
            allMetrics.AddRange(metrics);
            // Each metric takes two columns from D; a narrow tab gets empty ones to hold them.
            var columns = Math.Max(Lead + headers.Count, 3 + 2 * allMetrics.Count);
            var rows = new List<List<SheetCell>>();
            var heights = new List<int?>();
            void Add(List<SheetCell> row, string fill, int? height)
            {
                while (row.Count < columns) row.Add(new SheetCell(null, fill));
                rows.Add(row);
                heights.Add(height);
            }

            // Rows 1-2: the masthead.
            var labels = new List<SheetCell>
            {
                new(new SheetImage(IconBase + icon + ".png"), p.Masthead),
                new("ALTSTABLE / COMPANION", p.Masthead, p.Gold, Bold: true, FontSize: 9),
                new(null, p.Masthead),
            };
            var values = new List<SheetCell> { new(null, p.Masthead), new(title, p.Masthead, p.Text, Bold: true, FontSize: 18), new(null, p.Masthead) };
            foreach (var m in allMetrics)
            {
                labels.Add(new SheetCell(m.Label, p.Masthead, p.Secondary, FontSize: 8));
                labels.Add(new SheetCell(null, p.Masthead));
                values.Add(m.Value with { Fill = p.Masthead, Color = m.Value.Color ?? p.Text, FontSize = m.Value.FontSize ?? 12, Left = true });
                values.Add(new SheetCell(null, p.Masthead));
            }
            Add(labels, p.Masthead, MastheadLabelHeight);
            Add(values, p.Masthead, MastheadTitleHeight);

            // Row 3: the groups' labels, each over its first column; row 4: the column names.
            var band = new List<SheetCell>();
            foreach (var g in (Group[])[new("CHARACTER", 3), new("AFFILIATION", 2), .. groups])
            {
                band.Add(new SheetCell(g.Label, p.Band, p.Gold, Bold: true, FontSize: 9));
                for (var i = 1; i < g.Columns; i++) band.Add(new SheetCell(null, p.Band));
            }
            Add(band, p.Band, BandHeight);
            // The portrait's column is too narrow for its name beside a filter button: the name is its note.
            Add([HeaderCell("", "Portrait"), .. ((string[])["Character", "Account", "Class", "Faction"]).Select(h => HeaderCell(h)), .. headers],
                p.Header, headerHeight);

            // A row per character: shades in turn, a fine line where the faction changes.
            for (var i = 0; i < chars.Count; i++)
            {
                var c = chars[i];
                var fill = i % 2 == 0 ? p.RowA : p.RowB;
                List<SheetCell> row =
                [
                    AvatarCell(c, avatar, fill),
                    new(Str(c, "name"), fill, Str(c, "class") is { } cls && ClassColors.TryGetValue(cls, out var cc) ? p.Tint(cc) : p.Text, Bold: true),
                    new(Num(c, "accountNumber"), fill, p.Secondary, Center: true),
                    new(Title(Str(c, "class")), fill, p.Secondary),
                    new(Str(c, "faction"), fill, Str(c, "faction") switch { "Horde" => p.Horde, "Alliance" => p.Alliance, _ => p.Secondary }),
                    .. cells(c, fill),
                ];
                Add(row, fill, RowHeight);
                if (i > 0 && Side(c) != Side(chars[i - 1]))
                    rows[^1] = [.. rows[^1].Select(x => x with { BorderTop = p.Divider })];
            }
            var body = rows.Count;
            if (footer is not null && chars.Count > 0)
                foreach (var f in footer()) Add(f, f.FirstOrDefault()?.Fill ?? p.RowA, RowHeight - 7);

            // Widths: the portrait's, else the longest text below the masthead (a column name
            // needs room for its filter button too).
            var widths = Enumerable.Range(0, columns).Select(i => i == 0 ? AvatarSize
                : Math.Max(TextWidth(rows.Skip(MastheadRows), i), TextWidth([rows[MastheadRows - 1]], i) + 20)).ToList();
            return new SheetTable(title, rows, FrozenColumns: 3, FrozenRows: MastheadRows, RowHeights: heights,
                ColumnWidths: [.. widths.Select(w => (int?)w)], Merges: [new SheetMerge(0, 0, 2, 1)],
                Filter: new SheetFilter(MastheadRows - 1, body));
        }

        private SheetCell HeaderCell(string label, string? note = null) =>
            new(label, p.Header, p.Text, Bold: true, Note: note, Center: true, Wrap: true);

        public SheetCell Header(string label, string? note = null) => HeaderCell(label, note);

        // The render the app holds, else the race's silhouette: under level 10 the armory has
        // none. Its own picture links to the armory's full one: Sheets cannot enlarge a picture
        // on hover, so the link card shows on hover and a click opens it full size. Without
        // portraits from the armory, the column is there, empty.
        private SheetCell AvatarCell(Dictionary<object, object?> c, AvatarSource? avatar, string fill)
        {
            if (avatar is null) return new SheetCell(null, fill);
            var guid = Str(c, "guid");
            var drawn = Num(c, "level") >= Armory.MinLevel && guid is not null && avatar.Drawn.Contains(guid);
            var url = drawn ? Armory.AvatarUrl(guid!, avatar.Sd) : null;
            url ??= Armory.PlaceholderUrl(Str(c, "race"), Str(c, "faction"), Female(c));
            return new SheetCell(url is null ? null : new SheetImage(url), fill,
                Link: drawn ? Armory.Url(guid!, avatar.Sd) : null,
                Note: drawn ? "The armory's picture: click it for the full one" : null);
        }

        public SheetPalette Palette => p;
    }

    /// <summary>
    /// A column's width from its longest text, 10 pt (about 7.5 px a character, bold or not, and
    /// room either side). Measured 2026-11-19: Sheets' own fit cuts bold text short (a realm, a
    /// race, an enchant header), so the export sizes text columns itself. A wrapped label counts
    /// its longest line.
    /// </summary>
    internal static int TextWidth(IEnumerable<IReadOnlyList<SheetCell>> rows, int column)
    {
        var longest = rows.Select(r => column < r.Count ? Shown(r[column]) : 0).DefaultIfEmpty(0).Max();
        return Math.Clamp((int)Math.Ceiling(longest * 7.5) + 16, 32, 360);
    }

    // How many characters a cell shows: a date or a coin purse as its format, a number as written.
    private static int Shown(SheetCell c) => c.Value switch
    {
        string s => s.Split('\n').Max(l => l.Length),
        double when c.NumberFormat == CoinFormat => "00g 00s 00c".Length,
        double when c.NumberFormat is { } f && f.Contains('y') => "19 Nov 2026  18:39".Length,
        double d when c.NumberFormat is { } f && f.Contains('/') => d.ToString("0", CultureInfo.InvariantCulture).Length + f.Length - 5,
        double d => (c.NumberFormat == "0.0" ? Math.Round(d, 1).ToString("0.0", CultureInfo.InvariantCulture)
            : c.NumberFormat == "0%" ? Math.Round(d * 100).ToString("0'%'", CultureInfo.InvariantCulture)
            : d.ToString("#,##0", CultureInfo.InvariantCulture)).Length,
        _ => 0,
    };

    /// <summary>
    /// Characters: Level, iLvl, Rested XP, the coin purse, Last seen, Hearthstone, Guild, Race,
    /// Ruleset; at the end, each faction's count, levels and coin, and everyone's.
    /// </summary>
    internal static SheetTable BuildCharacters(IReadOnlyList<Dictionary<object, object?>> chars, long now,
        AvatarSource? avatar = null, SheetPalette? palette = null)
    {
        var p = palette ?? SheetPalette.Dark;
        var view = new View(Characters, "account-summary", now, avatar, p);
        // The coin purse is the copper balance, shown 1g 02s 69c: one column that sorts and adds.
        SheetCell Coin(double copper, string? fill, bool bold = false) =>
            new(copper, fill, p.Gold, Bold: bold, NumberFormat: CoinFormat, Center: true);
        var ilvls = chars.Select(c => Num(c, "ilvl")).OfType<double>().ToList();
        List<Metric> metrics =
        [
            new("CHARACTERS", new SheetCell((double)chars.Count, Bold: true)),
            new("TOTAL COIN", Coin(chars.Sum(c => Num(c, "money") ?? 0), null, true)),
            new("TOTAL LEVELS", new SheetCell(chars.Sum(c => Num(c, "level") ?? 0), Bold: true)),
            new("AVERAGE iLVL", ilvls.Count > 0 ? new SheetCell(Math.Round(ilvls.Average(), 1), Color: p.Cyan, Bold: true, NumberFormat: "0.0")
                : new SheetCell(null)),
        ];
        List<SheetCell> headers =
        [
            view.Header("Level"), view.Header("iLvl"),
            view.Header("Rested XP", "Rested XP as a share of a level, carried forward to the export as AltStable's own column does"),
            view.Header("Coin purse"), view.Header("Last seen"), view.Header("Hearthstone"), view.Header("Guild"),
            view.Header("Race"), view.Header("Ruleset", "The realm's ruleset; the realm's name, when AltStable knows it, is the cell's note"),
        ];
        IEnumerable<SheetCell> Row(Dictionary<object, object?> c, string fill)
        {
            var seen = Num(c, "lastUpdate");
            yield return new SheetCell(Num(c, "level"), fill, p.Text, Bold: true, Center: true);
            yield return Num(c, "ilvl") is double ilvl ? new SheetCell(Math.Round(ilvl, 1), fill, p.Cyan, NumberFormat: "0.0", Center: true) : new SheetCell(null, fill);
            // Percentage points to a share, once: 150 is 1.5, shown 150%.
            yield return RestedPercent(c, now) is double rested ? new SheetCell(rested / 100, fill, p.Text, NumberFormat: "0%", Center: true) : new SheetCell(null, fill);
            yield return Num(c, "money") is double money ? Coin(money, fill) : new SheetCell(null, fill);
            yield return seen is double s and > 0 ? new SheetCell(SheetDate(s), fill, p.Text, NumberFormat: DateFormat) : new SheetCell(null, fill);
            yield return new SheetCell(Str(c, "bind"), fill, p.Text);
            yield return new SheetCell(Str(c, "guild"), fill, p.Text);
            yield return new SheetCell(Str(c, "raceName") ?? Str(c, "race"), fill, p.Text);
            var realm = Str(c, "realm") is { Length: > 0 } r ? r : null;
            yield return new SheetCell(RulesetOf(c), fill, p.Text, Note: realm);
        }
        // Below the table, outside its filter: each faction's figures, then everyone's - the whole
        // export, whatever the filter hides.
        IReadOnlyList<List<SheetCell>> Footer()
        {
            const int count = Lead, levels = Lead + 1, coin = Lead + 3;
            List<SheetCell> Line(string? label, string fill, string color, bool bold, params (int At, SheetCell Cell)[] at)
            {
                var row = new List<SheetCell> { new(label, fill, color, Bold: bold) };
                foreach (var (col, cell) in at)
                {
                    while (row.Count < col) row.Add(new SheetCell(null, fill));
                    row.Add(cell with { Fill = fill });
                }
                return row;
            }
            SheetCell N(double v, bool bold = false) => new(v, null, p.Text, Bold: bold, Center: true);
            var lines = new List<List<SheetCell>>
            {
                Line(null, p.Masthead, p.Text, false),
                Line("FACTION TOTALS", p.Band, p.Gold, true, (count, view.Header("Characters") with { Fill = p.Band }),
                    (levels, view.Header("Levels") with { Fill = p.Band }), (coin, view.Header("Coin purse") with { Fill = p.Band })),
            };
            foreach (var side in chars.GroupBy(Side).OrderBy(g => g.Key))
            {
                var name = Str(side.First(), "faction") is { Length: > 0 } f && side.Key < 2 ? f : "Other";
                lines.Add(Line(name, p.RowA, side.Key switch { 0 => p.Horde, 1 => p.Alliance, _ => p.Secondary }, false,
                    (count, N(side.Count())), (levels, N(side.Sum(c => Num(c, "level") ?? 0))), (coin, Coin(side.Sum(c => Num(c, "money") ?? 0), null))));
            }
            lines.Add(Line("All characters", p.Band, p.Text, true,
                (count, N(chars.Count, true)), (levels, N(chars.Sum(c => Num(c, "level") ?? 0), true)), (coin, Coin(chars.Sum(c => Num(c, "money") ?? 0), null, true))));
            return lines;
        }
        return view.Build(chars, metrics, [new("PROGRESSION", 3), new("CURRENCY", 1), new("CHARACTER DETAILS", 5)], headers, Row, footer: Footer);
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

    /// <summary>
    /// Skills: Level, then the 13 skills - crafting, gathering, the secondary skills and riding -
    /// as "current / trained cap", the current rank the cell's number. Reaching the cap is gold
    /// on amber: time to train the next rank.
    /// </summary>
    internal static SheetTable BuildSkills(IReadOnlyList<Dictionary<object, object?>> chars, long now = 0,
        AvatarSource? avatar = null, SheetPalette? palette = null)
    {
        var p = palette ?? SheetPalette.Dark;
        var view = new View(Skills, "skills", now, avatar, p);
        var learned = chars.Sum(c => SkillColumns.Count(s => Num(c, s.Field) is > 0));
        List<Metric> metrics =
        [
            new("LEARNED SKILL ENTRIES", new SheetCell((double)learned, Bold: true)),
            new("SKILL RANKS", new SheetCell("Current / trained cap. Gold marks a capped skill.", FontSize: 10)),
        ];
        List<SheetCell> headers = [view.Header("Level"), .. SkillColumns.Select(s => view.Header(s.Label))];
        IEnumerable<SheetCell> Row(Dictionary<object, object?> c, string fill)
        {
            yield return new SheetCell(Num(c, "level"), fill, p.Text, Bold: true, Center: true);
            foreach (var (_, field, maxField) in SkillColumns)
            {
                // 0 is "not learned": an empty cell, as in the hand-kept sheet.
                if (Num(c, field) is not double v || v <= 0) { yield return new SheetCell(null, fill); continue; }
                var cap = Num(c, maxField) is double mx && mx > 0 ? mx : (double?)null;
                var capped = cap is double m && v >= m;
                yield return new SheetCell(v, capped ? p.CappedFill : fill, capped ? p.CappedText : p.Text, Bold: capped, Center: true,
                    NumberFormat: cap is double k ? "0\" / " + k.ToString(CultureInfo.InvariantCulture) + "\"" : null,
                    Note: cap is double n ? $"{v.ToString(CultureInfo.InvariantCulture)} / {n.ToString(CultureInfo.InvariantCulture)}" : null);
            }
        }
        return view.Build(chars, metrics,
            [new("PROGRESS", 1), new("CRAFTING", 6), new("GATHERING", 3), new("SECONDARY SKILLS & RIDING", 4)], headers, Row);
    }

    /// <summary>
    /// The pairs of one side's faction and the other's that bruk's template (blizzdata.gs,
    /// <c>foreverReps</c>) shows as one column - each character its own side's - plus the
    /// Forever pairs the addon's notes give (Reputations.lua: which side each is hidden from):
    /// Alliance first, Horde second.
    /// </summary>
    internal static readonly (int Alliance, int Horde)[] RepPairs =
    [
        (72, 76), (47, 81), (69, 68), (54, 530), (2779, 2778),     // the capitals, and Skyborne's
        (730, 729), (890, 889), (509, 510), (2799, 2798),           // the battlegrounds, and Darkspear Islands'
        (2586, 2587),                                               // the merchants
    ];

    // A Reputations column: one faction, or a pair (its Alliance and Horde halves).
    internal sealed record RepColumn(SheetReputation? Alliance, SheetReputation? Horde, SheetReputation? Neutral)
    {
        public IEnumerable<SheetReputation> All => new[] { Alliance, Horde, Neutral }.OfType<SheetReputation>();

        // The one a section of this faction sees: its own side's, else whichever there is.
        public SheetReputation For(string? faction) => faction switch
        {
            "Alliance" when Alliance is not null => Alliance,
            "Horde" when Horde is not null => Horde,
            _ => Neutral ?? Alliance ?? Horde!,
        };
    }

    /// <summary>The columns, in the addon's order: a pair where its first half falls.</summary>
    internal static IReadOnlyList<RepColumn> RepColumns()
    {
        var byId = AllReputations.ToDictionary(r => r.Id);
        var columns = new List<RepColumn>();
        foreach (var r in AllReputations)
        {
            if (RepPairs.FirstOrDefault(p => p.Alliance == r.Id || p.Horde == r.Id) is var (a, h) && a != 0)
            {
                if (columns.Any(c => c.Alliance?.Id == a)) continue;
                columns.Add(new RepColumn(byId[a], byId[h], null));
            }
            else
            {
                columns.Add(new RepColumn(null, null, r));
            }
        }
        return columns;
    }

    /// <summary>
    /// The reputations any character has met (the addon's <c>RepFieldsInUse</c>), the pairs of one
    /// side's faction and the other's first, then the shared ones, each in the addon's order. A
    /// pair's heading is the Horde name over the Alliance one, and each character's cell is its
    /// own side's standing, by its Faction. A standing in full; its faction and standing in the note.
    /// </summary>
    internal static SheetTable BuildReputations(IReadOnlyList<Dictionary<object, object?>> chars, long now = 0,
        AvatarSource? avatar = null, SheetPalette? palette = null)
    {
        var p = palette ?? SheetPalette.Dark;
        var view = new View(Reputations, "reputations", now, avatar, p);
        static double? Standing(Dictionary<object, object?> c, SheetReputation r) =>
            Num(c, "rep_" + r.Id.ToString(CultureInfo.InvariantCulture)) is double v && v is >= 1 and <= 8 ? v : null;
        var used = RepColumns().Where(col => col.All.Any(r => chars.Any(c => Standing(c, r) is not null))).ToList();
        var pairs = used.Where(col => col.Neutral is null).ToList();
        var shared = used.Where(col => col.Neutral is not null).ToList();
        List<RepColumn> ordered = [.. pairs, .. shared];
        var recorded = chars.Sum(c => AllReputations.Count(r => Standing(c, r) is not null));
        List<Metric> metrics = [new("RECORDED STANDINGS", new SheetCell((double)recorded, Bold: true))];
        if (pairs.Count > 0)
            metrics.Add(new("PAIRED HEADINGS", new SheetCell("Read the upper name for Horde and the lower name for Alliance.", FontSize: 10)));
        List<SheetCell> headers = [.. ordered.Select(col => col.Neutral is { } n ? view.Header(n.Label)
            : view.Header($"{col.Horde!.Label}\n{col.Alliance!.Label}", "Horde above, Alliance below: each character's own side"))];
        IEnumerable<SheetCell> Row(Dictionary<object, object?> c, string fill)
        {
            foreach (var col in ordered)
            {
                // Its own side's; a character that somehow has only the other side's still shows
                // it, marked, the note naming which.
                var mine = col.For(Str(c, "faction"));
                var r = Standing(c, mine) is not null ? mine : col.All.FirstOrDefault(x => Standing(c, x) is not null);
                if (r is null || Standing(c, r) is not double v)
                {
                    yield return new SheetCell(null, fill);
                    continue;
                }
                var (text, tint) = p.Standings[(int)v];
                var other = r != mine;
                yield return new SheetCell(StandingNames[(int)v] + (other ? " *" : ""), tint ?? fill, text, Bold: tint is not null, Center: true,
                    Note: $"{r.Label}: {StandingNames[(int)v]}" + (other ? " (the other side's faction)" : ""));
            }
        }
        List<Group> groups = [];
        if (pairs.Count > 0) groups.Add(new("FACTION PAIRS / HORDE ABOVE ALLIANCE", pairs.Count));
        if (shared.Count > 0) groups.Add(new("SHARED FACTIONS", shared.Count));
        return view.Build(chars, metrics, groups, headers, Row, headerHeight: pairs.Count > 0 ? PairHeaderHeight : HeaderHeight);
    }

    /// <summary>
    /// Gear: the character's item level, the 17 slots in the paper doll's order - armour and
    /// neck, rings and trinkets, weapons - each its item level in its quality's colour (the item,
    /// its enchant and its quality in words as the note, its Wowhead page as the link), then the
    /// enchants, for the slots any character has one on.
    /// </summary>
    internal static SheetTable BuildGear(IReadOnlyList<Dictionary<object, object?>> chars, long now = 0,
        AvatarSource? avatar = null, SheetPalette? palette = null)
    {
        var p = palette ?? SheetPalette.Dark;
        var view = new View(Gear, "gear-progression", now, avatar, p);
        var labels = GearSlots.ToDictionary(s => s.Key, s => s.Label, StringComparer.Ordinal);
        var enchants = EnchantSlots.Where(s => chars.Any(c => Str(c, "gearench_" + s) is { Length: > 0 })).ToList();
        var equipped = chars.Sum(c => GearSlots.Count(s => Occupied(c, s.Key)));
        List<Metric> metrics =
        [
            new("EQUIPPED ITEMS", new SheetCell((double)equipped, Bold: true)),
            new("ITEM DETAILS", new SheetCell("Hover for item and enchant. Click for Wowhead.", FontSize: 10)),
        ];
        List<SheetCell> headers =
        [
            view.Header("iLvl"), .. GearSlots.Select(s => view.Header(s.Label)),
            .. enchants.Select(s => view.Header(labels[s] + " enchant")),
        ];
        IEnumerable<SheetCell> Row(Dictionary<object, object?> c, string fill)
        {
            yield return Num(c, "ilvl") is double ilvl ? new SheetCell(Math.Round(ilvl, 1), fill, p.Cyan, Bold: true, NumberFormat: "0.0", Center: true) : new SheetCell(null, fill);
            foreach (var (key, _) in GearSlots) yield return ItemCell(c, key, fill, p);
            foreach (var key in enchants)
                yield return new SheetCell(Str(c, "gearench_" + key) is { Length: > 0 } ench ? ench : null, fill, p.Secondary);
        }
        List<Group> groups = [new("GEAR", 1), new("ARMOUR & NECK", 10), new("RINGS & TRINKETS", 4), new("WEAPONS", 3)];
        if (enchants.Count > 0) groups.Add(new("ENCHANTS", enchants.Count));
        return view.Build(chars, metrics, groups, headers, Row);
    }

    // Something worn there: an item id, or a name.
    private static bool Occupied(Dictionary<object, object?> c, string key) =>
        (Num(c, "gearid_" + key) ?? 0) > 0 || Str(c, "gearname_" + key) is { Length: > 0 };

    // The item's level in its quality's colour, its name, enchant and quality as the note. A slot
    // whose name the client had not cached at scan time, or whose level is unknown, still shows
    // that something is there.
    private static SheetCell ItemCell(Dictionary<object, object?> c, string key, string fill, SheetPalette p)
    {
        if (!Occupied(c, key)) return new SheetCell(null, fill);
        var id = Num(c, "gearid_" + key) ?? 0;
        var name = Str(c, "gearname_" + key) is { Length: > 0 } n ? n : "item " + id.ToString(CultureInfo.InvariantCulture);
        var q = (int)(Num(c, "gearq_" + key) ?? 1);
        if (q < 0 || q >= p.Quality.Count) q = 1;
        var note = new List<string> { name };
        if (Str(c, "gearench_" + key) is { Length: > 0 } ench) note.Add("Enchanted: " + ench);
        note.Add("Quality: " + QualityNames[q]);
        object value = Num(c, "gear_" + key) is double ilvl and > 0 ? ilvl : "?";
        return new SheetCell(value, fill, p.Quality[q], Note: string.Join('\n', note), Center: true,
            Link: id > 0 ? WowheadItem + id.ToString(CultureInfo.InvariantCulture) : null);
    }

    /// <summary>
    /// The realm's ruleset as AltStable's own Ruleset column shows it - its badge: PvE, PvP, RP,
    /// RP-PvP, HC - or null for a realm it cannot read. A port of the addon's
    /// <c>AltStable.RulesetOf</c> (Config.lua, AltStable 1.9.0) and <c>RULESET_BADGE</c>: words,
    /// not substrings ("rp" inside a word is not RP); RP with PvP anywhere is RP-PvP. The owner
    /// wants the ruleset in the cell and the realm's name only on hover (Codex on #85: a last-word
    /// parse got "Forever Roleplay PvP" and "Classic Beta PvP 2" wrong).
    /// </summary>
    /// <summary>
    /// A character's ruleset badge: the <c>ruleset</c> AltStable 1.11.0 reads from the game's rules
    /// at each scan, else the one its realm's name gives, else none. Since the server hotfix of
    /// 2026-11-20 the game no longer names the realm: a character first seen after it has no
    /// <c>realm</c>, and a build from that evening stored <c>""</c> over a real one (AltStable
    /// #408 / #412). AltStable's keys, not its labels: "Normal" is PvE, "RPPvP" is RP-PvP; any
    /// other value is not known, and the realm decides.
    /// </summary>
    internal static string? RulesetOf(Dictionary<object, object?> c) =>
        Str(c, "ruleset") switch
        {
            "Normal" => "PvE",
            "PvP" => "PvP",
            "RP" => "RP",
            "RPPvP" => "RP-PvP",
            "Hardcore" => "HC",
            _ => Ruleset(Str(c, "realm")),
        };

    internal static string? Ruleset(string? realm)
    {
        if (string.IsNullOrWhiteSpace(realm)) return null;
        var words = " " + new string([.. realm.ToLowerInvariant().Select(ch => char.IsAsciiLetterOrDigit(ch) ? ch : ' ')]) + " ";
        bool Word(string w) => words.Contains(" " + w + " ", StringComparison.Ordinal);
        if (Word("hardcore") || Word("hc")) return "HC";
        if (Word("rppvp")) return "RP-PvP";
        var pvp = words.Contains("pvp", StringComparison.Ordinal);
        if (Word("rp") || Word("roleplay")) return pvp ? "RP-PvP" : "RP";
        return pvp ? "PvP" : "PvE";
    }

    // The export's time, as the masthead says it: this PC's wall clock.
    private static string LocalTime(long epoch) =>
        DateTimeOffset.FromUnixTimeSeconds(epoch).ToLocalTime().ToString("d MMM yyyy  HH:mm", CultureInfo.InvariantCulture);

    // sexID is 0 male, 1 female; an older record has only the word.
    private static bool Female(Dictionary<object, object?> c) =>
        Num(c, "sexID") is double id ? id == 1 : Str(c, "gender") == "Female";

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
