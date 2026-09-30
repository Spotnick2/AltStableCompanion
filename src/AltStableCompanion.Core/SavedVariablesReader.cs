using System.Globalization;

namespace AltStableCompanion.Core;

/// <summary>One screenshot the addon recorded (docs/PORTRAIT-CONTRACT.md, section 1).</summary>
public sealed record RenderRecord(
    string Guid,
    string Name,
    int Shot,
    DateTime Stamp,
    long? Epoch,
    int? ScreenW,
    int? ScreenH,
    string? Race,
    string? Class,
    string StorePath);

/// <summary>
/// What the addon's roster knows of a character (<c>AltStableDB[guid]</c>): what an enhanced
/// portrait's prompt says about them, and whether they are worth one. <see cref="Updated"/> is
/// the addon's <c>lastUpdate</c>, seconds since the epoch, for choosing between two accounts'
/// records of the same character.
/// </summary>
public sealed record RosterCharacter(string Guid, string Name, string? Class, string? Race, string? Gender, int Level, long Updated,
    string? RaceName = null);

/// <summary>One account's capture store. <see cref="Refused"/> is set for a version this app does not know.</summary>
public sealed record PortraitStore(string Path, int Version, IReadOnlyList<RenderRecord> Renders, string? Refused = null);

/// <summary>
/// One account's roster tables, read from the same text as its capture store at the same
/// moment - and read whatever the capture store said: an account that never captured still
/// hides characters, and hidden anywhere is hidden. <see cref="Problem"/> is set when a
/// table stopped mid-way: what it would have said is not known, and the tables it did give
/// are not a view of the account.
/// </summary>
public sealed record RosterStore(string Path, IReadOnlyList<RosterCharacter> Characters, IReadOnlySet<string> Hidden,
    string? Problem = null);

/// <summary>
/// Every account's file, read once each: the capture stores (an account with no captures has
/// none), the roster tables (every account has some), and the files that could not be read
/// this time - which anything that must not act on a partial view has to know.
/// </summary>
public sealed record SavedVariablesSnapshot(IReadOnlyList<PortraitStore> Stores, IReadOnlyList<RosterStore> Rosters,
    IReadOnlyList<string> Skipped);

public static class SavedVariablesReader
{
    public const string Global = "AltStablePortraits";
    public const string RosterGlobal = "AltStableDB";
    public const string ConfigGlobal = "AltStableConfig";
    public const string FileName = "AltStable.lua";
    public const int SupportedVersion = 1;

    /// <summary>
    /// Every account's AltStable.lua: <c>WTF\Account\*\SavedVariables\AltStable.lua</c> exactly.
    /// All of them - every client writes screenshots into the same folder, so a machine with two
    /// accounts needs both stores to know whose screenshots are whose.
    /// </summary>
    public static IEnumerable<string> FindStores(string accountsDir)
    {
        if (!Directory.Exists(accountsDir)) yield break;
        foreach (var account in Directory.EnumerateDirectories(accountsDir))
        {
            var path = Path.Combine(account, "SavedVariables", FileName);
            if (File.Exists(path)) yield return path;
        }
    }

    /// <summary>
    /// The store in <paramref name="text"/>, or null when there are no captures - no
    /// assignment, <c>AltStablePortraits = nil</c> (what the client writes for an account that
    /// never captured, measured), or an empty table. Throws
    /// <see cref="SavedVariablesFormatException"/> for a file that stops mid-table.
    /// </summary>
    public static PortraitStore? Parse(string text, string path)
    {
        var value = LuaTableScanner.ReadGlobal(text, Global, out _);
        if (value is not Dictionary<object, object?> table) return null;

        var version = table.TryGetValue("version", out var v) && v is double dv ? (int)dv : SupportedVersion;
        if (version > SupportedVersion)
        {
            return new PortraitStore(path, version, [],
                $"capture records are version {version}; this companion understands {SupportedVersion} - update it");
        }

        var renders = new List<RenderRecord>();
        if (table.TryGetValue("renders", out var r) && r is Dictionary<object, object?> list)
        {
            // Array order is recording order: sort by position, not dictionary order.
            foreach (var entry in list.Where(kv => kv.Key is long).OrderBy(kv => (long)kv.Key).Select(kv => kv.Value))
            {
                if (entry is not Dictionary<object, object?> e) continue;
                var guid = Str(e, "guid");
                var stamp = Str(e, "stamp");
                var shot = Num(e, "shot");
                // A record the contract cannot pair is not a record.
                if (guid is null || stamp is null || shot is null) continue;
                // The GUID ends up in the manifest, which the client RUNS. Every real one is
                // letters, digits and hyphens; anything else is not a GUID.
                if (!ManifestWriter.IsSafeKey(guid)) continue;
                if (!DateTime.TryParseExact(stamp, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out var when)) continue;
                renders.Add(new RenderRecord(
                    guid, Str(e, "name") ?? guid, (int)shot.Value, when,
                    Num(e, "epoch") is double ep ? (long)ep : null,
                    Num(e, "screenW") is double sw ? (int)sw : null,
                    Num(e, "screenH") is double sh ? (int)sh : null,
                    Str(e, "race"), Str(e, "class"), path));
            }
        }
        return new PortraitStore(path, version, renders);
    }

    /// <summary>
    /// The roster tables of one file. Never throws for what is in them - conversion does not
    /// depend on them - but a table that stops mid-way is named in <see cref="RosterStore.Problem"/>.
    /// </summary>
    public static RosterStore ParseRoster(string text, string path)
    {
        string? problem = null;
        IReadOnlyList<RosterCharacter> chars;
        IReadOnlySet<string> hidden;
        try
        {
            chars = ReadRoster(text);
        }
        catch (SavedVariablesFormatException ex)
        {
            chars = [];
            problem = $"{RosterGlobal}: {ex.Message}";
        }
        try
        {
            hidden = ReadHidden(text);
        }
        catch (SavedVariablesFormatException ex)
        {
            hidden = new HashSet<string>();
            problem = $"{ConfigGlobal}: {ex.Message}";
        }
        return new RosterStore(path, chars, hidden, problem);
    }

    // AltStableDB[guid] = { name, class, race, raceName, gender, level, lastUpdate, ... }: what
    // the roster knows. A record without a safe guid or a name is skipped; a missing level is
    // 0, a missing lastUpdate is 0. Anything odd in these tables is not a reason to stop.
    private static IReadOnlyList<RosterCharacter> ReadRoster(string text)
    {
        var chars = new List<RosterCharacter>();
        if (LuaTableScanner.ReadGlobal(text, RosterGlobal, out _) is not Dictionary<object, object?> db) return chars;
        foreach (var (key, entry) in db)
        {
            if (key is not string guid || !ManifestWriter.IsSafeKey(guid)) continue;
            if (entry is not Dictionary<object, object?> e) continue;
            var name = Str(e, "name");
            if (string.IsNullOrWhiteSpace(name)) continue;
            chars.Add(new RosterCharacter(guid, name, Str(e, "class"), Str(e, "race"), Str(e, "gender"),
                Num(e, "level") is double lv ? (int)lv : 0, Num(e, "lastUpdate") is double up ? (long)up : 0,
                Str(e, "raceName")));
        }
        return chars;
    }

    // AltStableConfig.hiddenCharacters = { [guid] = true }: the ones the roster does not draw.
    private static IReadOnlySet<string> ReadHidden(string text)
    {
        var hidden = new HashSet<string>(StringComparer.Ordinal);
        if (LuaTableScanner.ReadGlobal(text, ConfigGlobal, out _) is not Dictionary<object, object?> config) return hidden;
        if (!config.TryGetValue("hiddenCharacters", out var h) || h is not Dictionary<object, object?> set) return hidden;
        foreach (var (key, flag) in set)
        {
            if (key is string guid && flag is true) hidden.Add(guid);
        }
        return hidden;
    }

    /// <summary>
    /// Every store under <paramref name="accountsDir"/>. A file WoW is writing right now is
    /// retried a few times, then skipped for this pass - the next pass picks it up.
    /// </summary>
    public static IReadOnlyList<PortraitStore> ReadAll(string accountsDir, Action<string>? log = null) =>
        ReadAll(FindStores(accountsDir), log);

    /// <summary>The same, for files already found.</summary>
    public static IReadOnlyList<PortraitStore> ReadAll(IEnumerable<string> paths, Action<string>? log = null) =>
        Snapshot(paths, log).Stores;

    /// <summary>Every file once: its capture store, its roster tables, or its name among the skipped.</summary>
    public static SavedVariablesSnapshot Snapshot(string accountsDir, Action<string>? log = null) =>
        Snapshot(FindStores(accountsDir), log);

    public static SavedVariablesSnapshot Snapshot(IEnumerable<string> paths, Action<string>? log = null)
    {
        var stores = new List<PortraitStore>();
        var rosters = new List<RosterStore>();
        var skipped = new List<string>();
        foreach (var path in paths)
        {
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    var text = ReadShared(path);
                    // The roster first: it is never a reason to refuse, and an account whose
                    // capture store is nil, or too new, still hides characters.
                    rosters.Add(ParseRoster(text, path));
                    var store = Parse(text, path);
                    if (store is not null) stores.Add(store);
                    break;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SavedVariablesFormatException)
                {
                    if (attempt >= 5)
                    {
                        log?.Invoke($"skipped {path} this pass: {ex.Message}");
                        skipped.Add(path);
                        break;
                    }
                    Thread.Sleep(300);
                }
            }
        }
        return new SavedVariablesSnapshot(stores, rosters, skipped);
    }

    // WoW replaces the file (a .bak, then a rename) while we may be reading it; share
    // Delete as well as ReadWrite so our handle never blocks that, and close it at once.
    private static string ReadShared(string path)
    {
        using var s = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(s);
        return reader.ReadToEnd();
    }

    private static string? Str(Dictionary<object, object?> e, string key) =>
        e.TryGetValue(key, out var v) ? v as string : null;

    private static double? Num(Dictionary<object, object?> e, string key) =>
        e.TryGetValue(key, out var v) && v is double d ? d : null;
}
