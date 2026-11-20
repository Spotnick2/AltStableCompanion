using System.Globalization;

namespace AltStableCompanion.Core;

/// <summary>
/// One cell of an exported tab: a string, a number or a <see cref="SheetImage"/>, and how it
/// looks. Colours are "RRGGBB". <see cref="Link"/>: the cell's text links there (an item's
/// Wowhead page), the value staying what it is.
/// </summary>
public sealed record SheetCell(object? Value, string? Fill = null, string? Color = null, bool Bold = false,
    string? NumberFormat = null, string? Note = null, bool Vertical = false, bool Center = false, string? Link = null)
{
    public static readonly SheetCell Empty = new((object?)null);
}

/// <summary>A cell's picture, by its address: an <c>=IMAGE()</c> formula, so Google fetches it, not the app.</summary>
public sealed record SheetImage(string Url);

/// <summary>
/// The Characters tab's pictures, with portraits from the armory on: the HD or SD model, and the
/// characters whose render the app holds (<see cref="CutoutFolder.ArmoryRenders"/>). The others
/// get the armory's silhouette: an <c>=IMAGE()</c> of a render that is not there is no error,
/// just nothing (measured 2026-11-19), so the sheet cannot fall back on its own.
/// </summary>
public sealed record AvatarSource(bool Sd, IReadOnlySet<string> Drawn);

/// <summary>
/// One tab the app owns: rewritten whole on every export. <see cref="RowHeights"/> and
/// <see cref="ColumnWidths"/> are in pixels, one per row and per column; null is the default
/// height, and a column fitted to its contents.
/// </summary>
public sealed record SheetTable(string Title, IReadOnlyList<IReadOnlyList<SheetCell>> Rows, int FrozenColumns = 1,
    int FrozenRows = 1, IReadOnlyList<int?>? RowHeights = null, IReadOnlyList<int?>? ColumnWidths = null)
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

/// <summary>A reputation AltStable tracks (the addon's <c>Reputations.lua</c>); <see cref="Icon"/> its tabard, or null.</summary>
public sealed record SheetReputation(int Id, string Label, string Short, string? Icon);

/// <summary>
/// The Google Sheet export (#82): every account's <c>AltStableDB</c> as the Characters, Skills,
/// Reputations and Gear tabs. Pure - no Google here; <see cref="GoogleSheets"/> writes what this builds.
/// <para>
/// The look (the owner's choice, 2026-11-19, after a friend's sheet): dark, one section per
/// faction - its crest on a banner row, its own header row, then its characters, each row in
/// its class colour - and a "Last update" line on top. Every tab starts Name, avatar, class
/// icon, so a formula's column keeps its field whichever way the armory setting goes.
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

    /// <summary>Blizzard's icon server, which the armory's site uses: <c>&lt;icon&gt;.jpg</c>, 56 px.</summary>
    public const string IconHost = "https://render.worldofwarcraft.com/us/icons/56/";

    // The paper doll's order (the addon's AltStable.GEAR_SLOTS).
    internal static readonly (string Key, string Label)[] GearSlots =
    [
        ("head", "Head"), ("neck", "Neck"), ("shoulder", "Shoulder"), ("back", "Back"), ("chest", "Chest"),
        ("wrist", "Wrist"), ("hands", "Hands"), ("waist", "Waist"), ("legs", "Legs"), ("feet", "Feet"),
        ("ring1", "Ring 1"), ("ring2", "Ring 2"), ("trinket1", "Trinket 1"), ("trinket2", "Trinket 2"),
        ("mainhand", "Main Hand"), ("offhand", "Off Hand"), ("ranged", "Ranged"),
    ];

    // The slots an enchant goes on: bruk's template's "Enchants" columns, in the paper doll's order.
    internal static readonly string[] EnchantSlots = ["head", "shoulder", "back", "chest", "wrist", "hands", "legs", "feet", "mainhand", "offhand", "ranged"];

    // Label, the skill's field, its cap's field, its icon. Primary professions are
    // prof_<Name>/profmax_<Name> (each its own field since AltStable#177); the secondary ones
    // and riding have their own. Icons measured on the icon server 2026-11-19.
    internal static readonly (string Label, string Field, string MaxField, string Icon)[] SkillColumns =
    [
        ("Alchemy", "prof_Alchemy", "profmax_Alchemy", "trade_alchemy"),
        ("Blacksmithing", "prof_Blacksmithing", "profmax_Blacksmithing", "trade_blacksmithing"),
        ("Enchanting", "prof_Enchanting", "profmax_Enchanting", "trade_engraving"),
        ("Engineering", "prof_Engineering", "profmax_Engineering", "trade_engineering"),
        ("Leatherworking", "prof_Leatherworking", "profmax_Leatherworking", "trade_leatherworking"),
        ("Tailoring", "prof_Tailoring", "profmax_Tailoring", "trade_tailoring"),
        ("Herbalism", "prof_Herbalism", "profmax_Herbalism", "spell_nature_naturetouchgrow"),
        ("Mining", "prof_Mining", "profmax_Mining", "trade_mining"),
        ("Skinning", "prof_Skinning", "profmax_Skinning", "inv_misc_pelt_wolf_01"),
        ("Cooking", "cooking", "cookingMax", "inv_misc_food_15"),
        ("Fishing", "fishing", "fishingMax", "trade_fishing"),
        ("First Aid", "firstAid", "firstAidMax", "spell_holy_sealofsacrifice"),
        ("Riding", "riding", "ridingMax", "ability_mount_ridinghorse"),
    ];

    /// <summary>
    /// The addon's <c>AltStable.REPUTATIONS</c> (Reputations.lua, AltStable 1.9.0), in its order.
    /// A tabard is kept only where the icon server has it (measured 2026-11-19): Forever's own
    /// (Theramore, Darkspear Raiders, Hyjal, the two merchant factions) answer 403 there, and
    /// take the short label, as the addon does for a tabard its client cannot draw.
    /// </summary>
    internal static readonly SheetReputation[] AllReputations =
    [
        new(72, "Stormwind", "Stormw", "inv_misc_tournaments_tabard_human"),
        new(47, "Ironforge", "Ironfg", "inv_misc_tournaments_tabard_dwarf"),
        new(69, "Darnassus", "Darnas", "inv_misc_tournaments_tabard_nightelf"),
        new(54, "Gnomeregan Exiles", "Gnome", "inv_misc_tournaments_tabard_gnome"),
        new(2779, "High Order", "HiOrdr", null),
        new(76, "Orgrimmar", "Orgrim", "inv_misc_tournaments_tabard_orc"),
        new(81, "Thunder Bluff", "ThBluf", "inv_misc_tournaments_tabard_tauren"),
        new(68, "Undercity", "UndCty", "inv_misc_tournaments_tabard_scourge"),
        new(530, "Darkspear Trolls", "Dkspr", "inv_misc_tournaments_tabard_troll"),
        new(2778, "Windshapers", "Windsh", null),
        new(529, "Argent Dawn", "ArgDwn", null),
        new(609, "Cenarion Circle", "CenCir", null),
        new(59, "Thorium Brotherhood", "Thorim", null),
        new(576, "Timbermaw Hold", "Timbmw", null),
        new(749, "Hydraxian Waterlords", "Hydrax", null),
        new(270, "Zandalar Tribe", "Zandlr", null),
        new(910, "Brood of Nozdormu", "Nozdor", null),
        new(589, "Wintersaber Trainers", "Wntsbr", null),
        new(21, "Booty Bay", "BtyBay", null),
        new(369, "Gadgetzan", "Gadgtz", null),
        new(470, "Ratchet", "Ratcht", null),
        new(577, "Everlook", "Evrlk", null),
        new(87, "Bloodsail Buccaneers", "Bldsl", null),
        new(349, "Ravenholdt", "Ravnhd", null),
        new(809, "Shen'dralar", "Shndrl", null),
        new(909, "Darkmoon Faire", "Dkmoon", null),
        new(92, "Gelkis Clan Centaur", "Gelkis", null),
        new(93, "Magram Clan Centaur", "Magram", null),
        new(730, "Stormpike Guard", "Stmpke", null),
        new(729, "Frostwolf Clan", "Frstwf", null),
        new(890, "Silverwing Sentinels", "Slvwng", null),
        new(889, "Warsong Outriders", "Warsng", null),
        new(509, "League of Arathor", "LgArth", null),
        new(510, "The Defilers", "Defilr", null),
        new(2799, "Theramore Expeditionary Force", "Thrmor", null),
        new(2798, "Darkspear Raiders", "DkRaid", null),
        new(2719, "Cenarion Scouts", "CenSct", null),
        new(2740, "Kirin Tor", "KirTor", null),
        new(2747, "Barkskin Burrow", "Barksk", null),
        new(2758, "Nightclaw Druids", "Nghtcl", null),
        new(2765, "Guardians of Hyjal", "GdHyjl", null),
        new(2782, "Bolder'ok Clan", "Bldrok", null),
        new(2787, "Earthen Ring", "ErthRg", "inv_misc_tabard_earthenring"),
        new(2819, "The Watchers", "Watchr", null),
        new(2826, "Brotherhood of the Horse", "BroHrs", null),
        new(2827, "Powderfuse", "Pwdrfs", null),
        new(2586, "Azeroth Commerce Authority", "AzCmAu", null),
        new(2587, "Durotar Supply and Logistics", "DuSupL", null),
    ];

    // A standing, rep_<id> = 1 Hated .. 8 Exalted: its letter (the owner's sheet's), its name,
    // and the game's FACTION_BAR_COLORS.
    private static readonly (string Letter, string Name, string Color)[] Standings =
    [
        ("", "", ""),
        ("Ha", "Hated", "CC2222"), ("Ho", "Hostile", "FF0000"), ("U", "Unfriendly", "EE6622"), ("N", "Neutral", "FFFF00"),
        ("F", "Friendly", "00FF00"), ("H", "Honored", "00FF88"), ("R", "Revered", "00FFCC"), ("E", "Exalted", "00FFFF"),
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

    // The look.
    internal const string PageFill = "1F2733";      // the sheet's dark
    internal const string TitleFill = "151B24";     // the "Last update" line
    internal const string HeaderFill = "2C3A4F";    // a section's header row
    internal const string HeaderText = "E8EAED";
    private const string RowText = "111111";         // dark, bold, on a class colour
    private const string CappedFill = "FFF200";      // a skill at its current cap: time to train the next rank
    private static readonly Dictionary<string, string> BannerFills = new(StringComparer.Ordinal)
    {
        ["Horde"] = "4A1C24", ["Alliance"] = "1B2C4A",
    };
    private static readonly Dictionary<string, string> Crests = new(StringComparer.Ordinal)
    {
        ["Horde"] = "ui_hordeicon", ["Alliance"] = "ui_allianceicon",
    };

    /// <summary>The avatars' cell: half their 84 px, so they are crisp on a 200% screen.</summary>
    public const int AvatarSize = 42;
    internal const int IconSize = 36;      // a header row of icons, a banner row
    internal const int CoinSize = 18;      // a coin beside its number

    // Every tab's first three columns.
    private const int Lead = 3;

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
    /// leaves the picture column empty.
    /// </summary>
    public static IReadOnlyList<SheetTable> Build(IReadOnlyList<Dictionary<object, object?>> chars, long now,
        AvatarSource? avatar = null) =>
        [BuildCharacters(chars, now, avatar), BuildSkills(chars, now, avatar), BuildReputations(chars, now, avatar), BuildGear(chars, now, avatar)];

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

    /// <summary>
    /// A tab of sections: the "Last update" line, then for each side with characters, a banner
    /// row (its crest, and what <paramref name="banner"/> adds), the header row, and a row per
    /// character - the first three cells Name, avatar, class icon, then <paramref name="cells"/>.
    /// </summary>
    private sealed class Layout(string title, long now, AvatarSource? avatar)
    {
        private readonly List<IReadOnlyList<SheetCell>> _rows = [];
        private readonly List<int?> _heights = [];

        public SheetTable Build(IReadOnlyList<Dictionary<object, object?>> chars, IReadOnlyList<SheetCell> header,
            Func<Dictionary<object, object?>, string, IEnumerable<SheetCell>> cells,
            Func<IReadOnlyList<Dictionary<object, object?>>, string, IEnumerable<SheetCell>>? banner,
            IReadOnlyList<int?> widths, bool iconHeader) =>
            Build(chars, _ => header, header.Count, cells, banner, widths, iconHeader);

        /// <summary>As above, with each section's own header: <paramref name="header"/> of its faction.</summary>
        public SheetTable Build(IReadOnlyList<Dictionary<object, object?>> chars, Func<string, IReadOnlyList<SheetCell>> header, int headerCount,
            Func<Dictionary<object, object?>, string, IEnumerable<SheetCell>> cells,
            Func<IReadOnlyList<Dictionary<object, object?>>, string, IEnumerable<SheetCell>>? banner,
            IReadOnlyList<int?> widths, bool iconHeader)
        {
            var columns = Lead + headerCount;
            Add(Pad([new SheetCell("Last update: " + LocalTime(now), TitleFill, HeaderText, Bold: true)], columns, TitleFill), null);
            foreach (var side in chars.GroupBy(Side).OrderBy(g => g.Key))
            {
                var faction = Str(side.First(), "faction") ?? "";
                var fill = BannerFills.GetValueOrDefault(faction, HeaderFill);
                var crest = Crests.TryGetValue(faction, out var icon) ? new SheetCell(new SheetImage(IconHost + icon + ".jpg"), fill) : new SheetCell(null, fill);
                var bannerRow = new List<SheetCell> { new(faction.Length > 0 ? faction : "Other", fill, HeaderText, Bold: true), crest, new(null, fill) };
                if (banner is not null) bannerRow.AddRange(banner([.. side], fill));
                Add(Pad(bannerRow, columns, fill), IconSize);
                Add(Pad([new("Character", HeaderFill, HeaderText, Bold: true), new(null, HeaderFill), new(null, HeaderFill), .. header(faction)], columns, HeaderFill),
                    iconHeader ? IconSize : null);
                foreach (var c in side)
                {
                    var rowFill = ClassFill(c);
                    Add(Pad([NameCell(c), AvatarCell(c, avatar, rowFill), ClassIcon(c, rowFill), .. cells(c, rowFill)], columns, rowFill), AvatarSize);
                }
            }
            List<int?> fixedWidths = [null, AvatarSize, AvatarSize, .. widths];
            var sized = Enumerable.Range(0, columns).Select(i => fixedWidths.ElementAtOrDefault(i) ?? TextWidth(_rows.Skip(1), i)).ToList();
            // The "Last update" line runs on across the row, but a tab can be narrower than it -
            // an empty roster, a tab with no columns of its own (Codex on #85): Name takes the rest.
            var line = TextWidth(_rows.Take(1), 0);
            if (sized.Sum() < line) sized[0] += line - sized.Sum();
            return new SheetTable(title, _rows, FrozenColumns: Lead, FrozenRows: 1, RowHeights: _heights,
                ColumnWidths: [.. sized.Select(w => (int?)w)]);
        }

        private void Add(IReadOnlyList<SheetCell> row, int? height)
        {
            _rows.Add(row);
            _heights.Add(height);
        }

        // As long as the header, in the row's colour: a row is a band of colour, not cells with gaps.
        private static List<SheetCell> Pad(List<SheetCell> row, int columns, string fill)
        {
            while (row.Count < columns) row.Add(new SheetCell(null, fill));
            return row;
        }
    }

    /// <summary>
    /// A column's width from its longest text, bold 10 pt (about 7.5 px a character, and room
    /// either side). Measured 2026-11-19: Sheets' own fit cuts bold text short (a realm, a race,
    /// an enchant header), so the export sizes text columns itself. The "Last update" line is
    /// left out: it runs on into the empty cells beside it.
    /// </summary>
    internal static int TextWidth(IEnumerable<IReadOnlyList<SheetCell>> rows, int column)
    {
        var longest = rows.Select(r => column < r.Count ? Shown(r[column]) : 0).DefaultIfEmpty(0).Max();
        return Math.Clamp((int)Math.Ceiling(longest * 7.5) + 16, 32, 360);
    }

    // How many characters a cell shows: a date as its format, a number as written.
    private static int Shown(SheetCell c) => c.Value switch
    {
        string s => s.Length,
        double when c.NumberFormat is { } f && f.Contains('y') => f.Length,
        double d => (c.NumberFormat == "0.0" ? Math.Round(d, 1).ToString("0.0", CultureInfo.InvariantCulture)
            : d.ToString("#,##0", CultureInfo.InvariantCulture)).Length,
        _ => 0,
    };

    private static SheetCell Header(string label, string? note = null) =>
        new(label, HeaderFill, HeaderText, Bold: true, Note: note, Center: true);

    private static SheetCell HeaderIcon(string icon, string note) =>
        new(new SheetImage(IconHost + icon + ".jpg"), HeaderFill, Note: note);

    internal static SheetTable BuildCharacters(IReadOnlyList<Dictionary<object, object?>> chars, long now,
        AvatarSource? avatar = null)
    {
        SheetCell Coin(string fill, string icon) => new(new SheetImage(IconHost + icon + ".jpg"), fill);
        var header = new List<SheetCell>
        {
            Header("Level"), Header("iLvl"),
            Header("Rested", "Rested XP as a % of the next level, carried forward to the export as AltStable's own column does"),
            Header("Acc", "Which account: 1, 2, ... in the order of the WTF\\Account folders"),
            Header("Gold"), HeaderIcon("inv_misc_coin_01", "Gold"), Header(""), HeaderIcon("inv_misc_coin_03", "Silver"),
            Header(""), HeaderIcon("inv_misc_coin_05", "Copper"),
            HeaderIcon("inv_misc_rune_01", "Hearthstone: where it is set"), Header("Guild"), Header("Last seen"),
            Header("Race"), Header("Ruleset", "The realm's ruleset; its name is the cell's note"),
        };
        IEnumerable<SheetCell> Money(double copper, string fill, string text, bool bold)
        {
            var c = (long)copper;
            yield return new SheetCell((double)(c / 10000), fill, text, bold, "#,##0");
            yield return Coin(fill, "inv_misc_coin_01");
            yield return new SheetCell((double)(c / 100 % 100), fill, text, bold);
            yield return Coin(fill, "inv_misc_coin_03");
            yield return new SheetCell((double)(c % 100), fill, text, bold);
            yield return Coin(fill, "inv_misc_coin_05");
        }
        IEnumerable<SheetCell> Row(Dictionary<object, object?> c, string fill)
        {
            var seen = Num(c, "lastUpdate");
            yield return Number(Num(c, "level"), fill);
            yield return Num(c, "ilvl") is double ilvl ? new SheetCell(Math.Round(ilvl, 1), fill, RowText, true, "0.0") : new SheetCell(null, fill);
            yield return RestedPercent(c, now) is double rested ? new SheetCell(rested, fill, RowText, true, "0\"%\"") : new SheetCell(null, fill);
            yield return Number(Num(c, "accountNumber"), fill);
            foreach (var m in Num(c, "money") is double money ? Money(money, fill, RowText, true)
                         : Enumerable.Repeat(new SheetCell(null, fill), 6)) yield return m;
            yield return Text(Str(c, "bind"), fill);
            yield return Text(Str(c, "guild"), fill);
            yield return seen is double s and > 0 ? new SheetCell(SheetDate(s), fill, RowText, true, "yyyy-mm-dd hh:mm") : new SheetCell(null, fill);
            yield return Text(Str(c, "raceName") ?? Str(c, "race"), fill);
            yield return Str(c, "realm") is { Length: > 0 } realm ? Text(Ruleset(realm), fill) with { Note = realm } : new SheetCell(null, fill);
        }
        // The side's gold, on its banner.
        IEnumerable<SheetCell> Banner(IReadOnlyList<Dictionary<object, object?>> side, string fill) =>
            [new(null, fill), new(null, fill), new(null, fill), new(null, fill),
             .. Money(side.Sum(c => Num(c, "money") ?? 0), fill, HeaderText, true)];
        return new Layout(Characters, now, avatar).Build(chars, header, Row, Banner,
            [null, null, null, null, null, CoinSize, null, CoinSize, null, CoinSize, null, null, null, null, null], iconHeader: true);
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

    internal static SheetTable BuildSkills(IReadOnlyList<Dictionary<object, object?>> chars, long now = 0,
        AvatarSource? avatar = null)
    {
        List<SheetCell> header = [Header("Level"), .. SkillColumns.Select(s => HeaderIcon(s.Icon, s.Label))];
        IEnumerable<SheetCell> Row(Dictionary<object, object?> c, string fill)
        {
            yield return Number(Num(c, "level"), fill);
            foreach (var (_, field, maxField, _) in SkillColumns)
            {
                // 0 is "not learned": an empty cell, as in the hand-kept sheet.
                if (Num(c, field) is not double v || v <= 0) { yield return new SheetCell(null, fill); continue; }
                var max = Num(c, maxField);
                var capped = max is double mx && mx > 0 && v >= mx;
                yield return new SheetCell(v, capped ? CappedFill : fill, RowText, Bold: true, Center: true,
                    Note: max is double cap && cap > 0 ? $"{v.ToString(CultureInfo.InvariantCulture)} / {cap.ToString(CultureInfo.InvariantCulture)}" : null);
            }
        }
        return new Layout(Skills, now, avatar).Build(chars, header, Row, null,
            [null, .. SkillColumns.Select(_ => (int?)IconSize + 8)], iconHeader: true);
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
    /// The reputations any character has met (the addon's <c>RepFieldsInUse</c>: a column no
    /// character has would only ever be blank), as the owner's sheet had them: a letter, here in
    /// its standing's colour. A pair of one side's faction and the other's is one column, as in
    /// bruk's template: each section's header shows its own side's tabard (or short name), and
    /// each character its own side's standing.
    /// </summary>
    internal static SheetTable BuildReputations(IReadOnlyList<Dictionary<object, object?>> chars, long now = 0,
        AvatarSource? avatar = null)
    {
        static double? Standing(Dictionary<object, object?> c, SheetReputation r) =>
            Num(c, "rep_" + r.Id.ToString(CultureInfo.InvariantCulture)) is double v && v is >= 1 and <= 8 ? v : null;
        var used = RepColumns().Where(col => col.All.Any(r => chars.Any(c => Standing(c, r) is not null))).ToList();
        IReadOnlyList<SheetCell> Header(string faction) =>
            [.. used.Select(col => col.For(faction) is var r && r.Icon is { } icon
                ? HeaderIcon(icon, string.Join(" / ", col.All.Select(x => x.Label)))
                : SheetExport.Header(r.Short, string.Join(" / ", col.All.Select(x => x.Label))))];
        IEnumerable<SheetCell> Row(Dictionary<object, object?> c, string fill)
        {
            foreach (var col in used)
            {
                // Its own side's; a character that somehow has only the other side's still shows it.
                var mine = col.For(Str(c, "faction"));
                var r = Standing(c, mine) is not null ? mine : col.All.FirstOrDefault(x => Standing(c, x) is not null);
                if (r is null || Standing(c, r) is not double v)
                {
                    yield return new SheetCell(null, fill);
                    continue;
                }
                var (letter, name, color) = Standings[(int)v];
                yield return new SheetCell(letter, color, RowText, Bold: true, Note: $"{r.Label}: {name}", Center: true);
            }
        }
        return new Layout(Reputations, now, avatar).Build(chars, Header, used.Count, Row, null,
            [.. used.Select(col => (int?)(col.All.Any(r => r.Icon is not null) ? IconSize + 8 : null))],
            iconHeader: used.Any(col => col.All.Any(r => r.Icon is not null)));
    }

    /// <summary>
    /// Bruk's template's gear view: the average item level, each slot's item level in its
    /// quality's colour (the item's name and enchant are the cell's note), then the enchant on
    /// each slot that takes one. On dark, not the class colour: quality colours need it.
    /// </summary>
    internal static SheetTable BuildGear(IReadOnlyList<Dictionary<object, object?>> chars, long now = 0,
        AvatarSource? avatar = null)
    {
        var labels = GearSlots.ToDictionary(s => s.Key, s => s.Label, StringComparer.Ordinal);
        // Only the slots some character has an enchant on, as reputations only those met.
        var enchants = EnchantSlots.Where(s => chars.Any(c => Str(c, "gearench_" + s) is { Length: > 0 })).ToList();
        List<SheetCell> header =
        [
            Header("iLvl"), .. GearSlots.Select(s => Header(s.Label)),
            .. enchants.Select(s => Header(labels[s] + " enchant")),
        ];
        IEnumerable<SheetCell> Row(Dictionary<object, object?> c, string fill)
        {
            yield return Num(c, "ilvl") is double ilvl ? new SheetCell(Math.Round(ilvl, 1), PageFill, HeaderText, true, "0.0", Center: true) : new SheetCell(null, PageFill);
            foreach (var (key, _) in GearSlots) yield return ItemCell(c, key);
            foreach (var key in enchants)
                yield return Str(c, "gearench_" + key) is { Length: > 0 } ench ? new SheetCell(ench, PageFill, HeaderText) : new SheetCell(null, PageFill);
        }
        return new Layout(Gear, now, avatar).Build(chars, header, Row, null,
            [], iconHeader: false);
    }

    // The item's level in its quality's colour, its name and enchant as the cell's note. A slot
    // whose name the client had not cached at scan time still shows that something is there.
    private static SheetCell ItemCell(Dictionary<object, object?> c, string key)
    {
        var id = Num(c, "gearid_" + key) ?? 0;
        var name = Str(c, "gearname_" + key);
        if (string.IsNullOrEmpty(name))
        {
            if (id <= 0) return new SheetCell(null, PageFill);
            name = "item " + id.ToString(CultureInfo.InvariantCulture);
        }
        var q = (int)(Num(c, "gearq_" + key) ?? 1);
        var color = q >= 0 && q < QualityColors.Length ? QualityColors[q] : QualityColors[1];
        var note = new List<string> { name };
        if (Str(c, "gearench_" + key) is { Length: > 0 } ench) note.Add("Enchanted: " + ench);
        object value = Num(c, "gear_" + key) is double ilvl and > 0 ? ilvl : "?";
        return new SheetCell(value, PageFill, color, Bold: true, Note: string.Join('\n', note), Center: true,
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

    private static string ClassFill(Dictionary<object, object?> c) =>
        Str(c, "class") is { } cls && ClassColors.TryGetValue(cls, out var fill) ? fill : HeaderFill;

    private static SheetCell NameCell(Dictionary<object, object?> c) =>
        new(Str(c, "name"), ClassFill(c), ClassColors.ContainsKey(Str(c, "class") ?? "") ? RowText : HeaderText, Bold: true);

    private static SheetCell ClassIcon(Dictionary<object, object?> c, string fill) =>
        Str(c, "class") is { Length: > 0 } cls && ClassColors.ContainsKey(cls)
            ? new SheetCell(new SheetImage(IconHost + "classicon_" + cls.ToLowerInvariant() + ".jpg"), fill, Note: Title(cls))
            : new SheetCell(null, fill);

    // The render the app holds, else the race's silhouette: under level 10 the armory has none.
    // Without portraits from the armory, the column is there, empty.
    private static SheetCell AvatarCell(Dictionary<object, object?> c, AvatarSource? avatar, string fill)
    {
        if (avatar is null) return new SheetCell(null, fill);
        var url = Num(c, "level") >= Armory.MinLevel && Str(c, "guid") is { } guid && avatar.Drawn.Contains(guid)
            ? Armory.AvatarUrl(guid, avatar.Sd)
            : null;
        url ??= Armory.PlaceholderUrl(Str(c, "race"), Str(c, "faction"), Female(c));
        return new SheetCell(url is null ? null : new SheetImage(url), fill);
    }

    // sexID is 0 male, 1 female; an older record has only the word.
    private static bool Female(Dictionary<object, object?> c) =>
        Num(c, "sexID") is double id ? id == 1 : Str(c, "gender") == "Female";

    private static SheetCell Text(string? s, string fill) =>
        new(string.IsNullOrEmpty(s) ? null : s, fill, RowText, Bold: true);

    private static SheetCell Number(double? d, string fill) =>
        new(d, fill, RowText, Bold: true, Center: true);

    // "DRUID" -> "Druid", "DEATHKNIGHT" -> "Deathknight": the class token, readable.
    private static string? Title(string? token) =>
        string.IsNullOrEmpty(token) ? token : token[..1].ToUpperInvariant() + token[1..].ToLowerInvariant();

    private static string LocalTime(long epoch) =>
        DateTimeOffset.FromUnixTimeSeconds(epoch).ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

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
