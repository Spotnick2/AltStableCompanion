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

/// <summary>One account's capture store. <see cref="Refused"/> is set for a version this app does not know.</summary>
public sealed record PortraitStore(string Path, int Version, IReadOnlyList<RenderRecord> Renders, string? Refused = null);

public static class SavedVariablesReader
{
    public const string Global = "AltStablePortraits";
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
    /// Every store under <paramref name="accountsDir"/>. A file WoW is writing right now is
    /// retried a few times, then skipped for this pass - the next pass picks it up.
    /// </summary>
    public static IReadOnlyList<PortraitStore> ReadAll(string accountsDir, Action<string>? log = null)
    {
        var stores = new List<PortraitStore>();
        foreach (var path in FindStores(accountsDir))
        {
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    var store = Parse(ReadShared(path), path);
                    if (store is not null) stores.Add(store);
                    break;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SavedVariablesFormatException)
                {
                    if (attempt >= 5)
                    {
                        log?.Invoke($"skipped {path} this pass: {ex.Message}");
                        break;
                    }
                    Thread.Sleep(300);
                }
            }
        }
        return stores;
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
