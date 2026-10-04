using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace AltStableCompanion.Core;

/// <summary>What the app knows of itself, for a diagnostics file. The rest is read off the disk.</summary>
/// <param name="HowFound">How the WoW folder was chosen, in a few words.</param>
/// <param name="CodexStatus">The controller's: null is not found, anything else is found.</param>
public sealed record DiagnosticsFacts(
    string AppVersion,
    WowInstall? Install,
    string HowFound,
    bool Enhance,
    bool CodexProbed,
    string? CodexStatus,
    string LogPath);

/// <summary>
/// Help, "Save diagnostics": ONE text file a player can attach to an issue. No images, no
/// upload, and the account folder names in <c>WTF\Account</c> - Battle.net account
/// identifiers - never in it: they become Account1..n, and the profile path becomes
/// %USERPROFILE%. Character names and GUIDs ARE in it, and the window says so.
/// </summary>
public static partial class Diagnostics
{
    public const int LogLines = 300;

    /// <summary>What the window says before anything is written.</summary>
    public const string Explanation =
        "Writes one text file to your Desktop for a bug report: the app and Windows versions, the WoW folder, "
        + "the AltStable versions, whether enhanced portraits are on, how many captures, portraits and enhanced "
        + "pictures each character has, and the last 300 lines of the log. Your characters' names and IDs are in it. "
        + "Your account folder names and your Windows user folder are not. No pictures, and nothing is sent: "
        + "attaching it is up to you.";

    /// <summary>The whole file, masked. Reads the disk; writes nothing.</summary>
    public static string Build(DiagnosticsFacts facts, DateTime now, string userProfile)
    {
        var sb = new StringBuilder();
        sb.AppendLine("AltStable Companion diagnostics");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Written {now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"App:        {PassText.VersionLine(facts.AppVersion)}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Windows:    {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})");

        var install = facts.Install;
        sb.AppendLine(CultureInfo.InvariantCulture, $"WoW folder: {install?.FlavorDir ?? "none"} ({facts.HowFound})");
        if (install is not null)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"AltStable:  {TocVersion(Path.Combine(install.AddOnsDir, "AltStable", "AltStable.toc"))}");
            var roster = TocVersion(Path.Combine(install.AddOnsDir, "AltStableRoster", "AltStableRoster.toc"));
            sb.AppendLine(CultureInfo.InvariantCulture, $"Roster:     {roster}{(install.RosterDrawsEnhanced ? ", draws enhanced pictures" : "")}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"Cutouts:    {(File.Exists(new CutoutFolder(install.CutoutAddonDir).TocPath) ? "the addon is there" : "no addon yet")}");
        }
        sb.AppendLine(CultureInfo.InvariantCulture, $"Enhanced:   {(facts.Enhance ? "on" : "off")}");
        // Found or not, and signed in or not: what `codex login status` says in full is the
        // account's business.
        sb.AppendLine(CultureInfo.InvariantCulture, $"Codex:      {(!facts.CodexProbed ? "not looked for" : facts.CodexStatus is null ? "not found"
            : facts.CodexStatus.StartsWith("Logged in", StringComparison.OrdinalIgnoreCase) ? "found, signed in" : "found")}");

        var accounts = new List<string>();
        if (install is not null)
        {
            accounts = AccountFolders(install.AccountsDir);
            sb.AppendLine();
            Characters(sb, install, accounts.Count);
        }

        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"Last {LogLines} log lines");
        foreach (var line in LogTail(facts.LogPath, LogLines)) sb.AppendLine(line);

        return Mask(sb.ToString(), accounts, userProfile);
    }

    /// <summary>Write <paramref name="text"/> to the folder, under a name of its own; the path written.</summary>
    public static string Save(string text, string folder, DateTime now)
    {
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, $"AltStableCompanion-diagnostics-{now:yyyyMMdd-HHmmss}.txt");
        File.WriteAllText(path, text);
        return path;
    }

    /// <summary>
    /// Every account folder becomes Account1..n - the ones given, in order, then any other the
    /// text names under <c>WTF\Account</c> (a log line from another install) - wherever it
    /// stands on its own, not only in a path. Then the profile folder becomes %USERPROFILE%.
    /// </summary>
    public static string Mask(string text, IEnumerable<string> accounts, string userProfile)
    {
        var names = new List<string>();
        void Add(string name)
        {
            if (name.Length > 0 && !names.Contains(name, StringComparer.OrdinalIgnoreCase)) names.Add(name);
        }
        foreach (var a in accounts) Add(a);
        foreach (Match m in AccountPath().Matches(text)) Add(m.Groups[1].Value);

        // Longest first: an account named inside another's name must not leave the rest behind.
        var masks = names.Select((n, i) => (Name: n, Mask: "Account" + (i + 1).ToString(CultureInfo.InvariantCulture)))
            .OrderByDescending(p => p.Name.Length);
        foreach (var (name, mask) in masks)
        {
            text = Regex.Replace(text, $@"(?<![A-Za-z0-9#])({Regex.Escape(name)})(?![A-Za-z0-9#])", mask,
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        var profile = Path.TrimEndingDirectorySeparator(userProfile);
        if (profile.Length > 0)
        {
            text = Regex.Replace(text, Regex.Escape(profile) + @"(?=$|[\\/\s""'),;:])", "%USERPROFILE%",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Multiline);
        }
        return text;
    }

    // A WTF\Account\<name> in a path, either slash. The name ends at the next separator, a
    // quote or the end of the line - an account folder can hold a space.
    [GeneratedRegex(@"WTF[\\/]Account[\\/]([^\\/\r\n""]+)", RegexOptions.IgnoreCase)]
    private static partial Regex AccountPath();

    /// <summary>The account folders, by name, sorted. Not <c>SavedVariables</c>, which is WoW's own.</summary>
    public static List<string> AccountFolders(string accountsDir)
    {
        try
        {
            if (!Directory.Exists(accountsDir)) return [];
            return [.. Directory.EnumerateDirectories(accountsDir)
                .Select(Path.GetFileName)
                .OfType<string>()
                .Where(n => !n.Equals("SavedVariables", StringComparison.OrdinalIgnoreCase))
                .Order(StringComparer.OrdinalIgnoreCase)];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>The <c>## Version:</c> line of a .toc, or why there is none.</summary>
    public static string TocVersion(string tocPath)
    {
        try
        {
            if (!File.Exists(tocPath)) return "not installed";
            foreach (var line in File.ReadLines(tocPath))
            {
                var t = line.Trim();
                if (t.StartsWith("## Version:", StringComparison.OrdinalIgnoreCase)) return t["## Version:".Length..].Trim();
            }
            return "installed, no version line";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"could not be read ({ex.Message})";
        }
    }

    // Per character: captures in the records, cutouts on disk, enhanced pictures on disk. By
    // GUID; a cutout without one is a line of its own, under its file name.
    private static void Characters(StringBuilder sb, WowInstall install, int accounts)
    {
        var snapshot = SavedVariablesReader.Snapshot(install.AccountsDir);
        var refused = snapshot.Stores.Count(s => s.Refused is not null);
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"Accounts:   {accounts}, with AltStable.lua: {snapshot.Rosters.Count}, capture records: {snapshot.Stores.Count}"
            + $"{(refused > 0 ? $" ({refused} of a version this app does not read)" : "")}"
            + $"{(snapshot.Skipped.Count > 0 ? $", could not be read: {snapshot.Skipped.Count}" : "")}");

        var rows = new Dictionary<string, Counts>(StringComparer.Ordinal);
        void Bump(string key, string name, int captures = 0, int cutouts = 0, int enhanced = 0)
        {
            var r = rows.TryGetValue(key, out var was) ? was : new Counts(name, 0, 0, 0);
            rows[key] = new Counts(r.Name, r.Captures + captures, r.Cutouts + cutouts, r.Enhanced + enhanced);
        }

        // Newest first: a renamed character goes by its current name.
        foreach (var c in CapturePairing.Pair(snapshot.Stores).OrderByDescending(c => c.OrderKey)) Bump(c.Guid, c.Name, captures: 1);

        var folder = new CutoutFolder(install.CutoutAddonDir);
        try
        {
            if (Directory.Exists(folder.CutoutsDir))
            {
                foreach (var tga in Directory.EnumerateFiles(folder.CutoutsDir, "*.tga"))
                {
                    var fileBase = Path.GetFileNameWithoutExtension(tga);
                    var guid = folder.ReadMeta(fileBase)?.Guid;
                    Bump(guid ?? "file " + fileBase, Collection.Label(fileBase), cutouts: 1);
                }
            }
            if (Directory.Exists(folder.EnhancedDir))
            {
                foreach (var tga in Directory.EnumerateFiles(folder.EnhancedDir, "*.tga"))
                {
                    var fileBase = Path.GetFileNameWithoutExtension(tga);
                    var guid = folder.ReadMeta(fileBase)?.Guid ?? folder.ReadEnhancedMeta(fileBase)?.Guid;
                    Bump(guid ?? "file " + fileBase, Collection.Label(fileBase), enhanced: 1);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"The cutouts could not be listed: {ex.Message}");
        }

        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"Characters: {rows.Count} (captures in the records, cutouts and enhanced pictures on disk)");
        foreach (var (key, r) in rows.OrderBy(p => p.Value.Name, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.Key, StringComparer.Ordinal))
        {
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"  {r.Name} ({key}): captures {r.Captures}, cutouts {r.Cutouts}, enhanced {r.Enhanced}");
        }
    }

    private sealed record Counts(string Name, int Captures, int Cutouts, int Enhanced);

    /// <summary>The last lines of the log, reaching into the rolled-over file when the current one is short.</summary>
    public static IReadOnlyList<string> LogTail(string logPath, int count)
    {
        var lines = new List<string>();
        foreach (var path in new[] { logPath + ".old", logPath })
        {
            try
            {
                if (!File.Exists(path)) continue;
                // The log may be being written: share it, as WoW's files are shared.
                using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(s);
                while (reader.ReadLine() is { } line) lines.Add(line);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                lines.Add($"({Path.GetFileName(path)} could not be read: {ex.Message})");
            }
        }
        if (lines.Count == 0) return ["(no log yet)"];
        return lines.Count <= count ? lines : lines[^count..];
    }
}
