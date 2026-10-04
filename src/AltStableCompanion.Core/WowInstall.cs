using Microsoft.Win32;

namespace AltStableCompanion.Core;

/// <summary>One WoW flavour folder, e.g. <c>...\World of Warcraft\_classic_beta_</c> (Forever).</summary>
public sealed record WowInstall(string FlavorDir)
{
    public string Root => Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(FlavorDir)) ?? FlavorDir;
    public string Flavor => Path.GetFileName(Path.TrimEndingDirectorySeparator(FlavorDir));
    public string Screenshots => Path.Combine(FlavorDir, "Screenshots");
    public string AccountsDir => Path.Combine(FlavorDir, "WTF", "Account");
    public string AddOnsDir => Path.Combine(FlavorDir, "Interface", "AddOns");
    public string CutoutAddonDir => Path.Combine(AddOnsDir, CutoutFolder.AddonName);
    public bool AltStableInstalled => File.Exists(Path.Combine(AddOnsDir, "AltStable", "AltStable.toc"));

    /// <summary>
    /// Whether the installed Roster can draw enhanced pictures: its .toc carries
    /// <c>## X-AltStable-Enhanced: 1</c> (docs/PORTRAIT-CONTRACT.md, section 3). Installed
    /// support, not a loaded plugin - the Roster is load-on-demand. Read before a generation
    /// is spent: a picture nothing draws is a waste.
    /// </summary>
    public bool RosterDrawsEnhanced => RosterDrawsEnhancedIn(Path.Combine(AddOnsDir, "AltStableRoster", "AltStableRoster.toc"));

    public static bool RosterDrawsEnhancedIn(string tocPath)
    {
        try
        {
            if (!File.Exists(tocPath)) return false;
            foreach (var line in File.ReadLines(tocPath))
            {
                var t = line.Trim();
                if (!t.StartsWith("## X-AltStable-Enhanced:", StringComparison.OrdinalIgnoreCase)) continue;
                return int.TryParse(t["## X-AltStable-Enhanced:".Length..].Trim(), out var v) && v >= 1;
            }
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}

/// <summary>
/// Where WoW is. Measured on this owner's machine (2026-09-29): the registry's
/// <c>World of Warcraft\InstallPath</c> is whichever flavour ran LAST (<c>_anniversary_</c>),
/// so only its parent is useful; the <c>World of Warcraft\Beta\InstallPath</c> subkey names
/// <c>_classic_beta_</c>, which is Forever, and is tried first. Forever's executable is
/// <c>WowB.exe</c>, so a flavour folder is recognised by any <c>Wow*.exe</c>, not one name.
/// </summary>
public static class WowInstallLocator
{
    public const string DefaultFlavor = "_classic_beta_";
    private const string DefaultRoot = @"C:\Program Files (x86)\World of Warcraft";
    private const string BlizzardKey = @"SOFTWARE\WOW6432Node\Blizzard Entertainment\World of Warcraft";

    public static bool IsFlavorDir(string dir) =>
        Directory.Exists(dir) && Directory.EnumerateFiles(dir, "Wow*.exe").Any();

    /// <summary>Flavour folders under a root: <c>_name_</c> directories that hold a WoW executable.</summary>
    public static IReadOnlyList<string> FlavorsUnder(string root)
    {
        if (!Directory.Exists(root)) return [];
        return [.. Directory.EnumerateDirectories(root)
            .Where(d => Path.GetFileName(d) is { Length: > 2 } n && n.StartsWith('_') && n.EndsWith('_'))
            .Where(IsFlavorDir)
            .Order(StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>
    /// The install to use, found through the registry or the default location. Null when none
    /// is. It takes no folder to prefer: one that fell back to this when the folder was wrong
    /// is how a pass ends up in the wrong game. <see cref="ResolvedInstall"/> decides.
    /// </summary>
    public static WowInstall? Detect() => Detect([.. CandidateFlavorDirs()]);

    /// <summary>
    /// Among these candidates, in their order: the first flavour folder where AltStable is
    /// installed - the game the player plays it in - looking at the candidates first, then at
    /// every flavour under their WoW folders. Where none has AltStable, the first candidate
    /// that is a flavour folder, as before. Never the newest: a time says nothing about which
    /// game the player means.
    /// </summary>
    public static WowInstall? Detect(IReadOnlyList<string> candidates)
    {
        if (WithAltStable(candidates) is [var first, ..]) return new WowInstall(first);
        foreach (var dir in candidates)
        {
            if (IsFlavorDir(dir)) return new WowInstall(dir);
        }
        return null;
    }

    /// <summary>
    /// The flavour folders that hold AltStable: the candidates first, in their order, then the
    /// others under the candidates' WoW folders, by name.
    /// </summary>
    public static IReadOnlyList<string> WithAltStable(IReadOnlyList<string> candidates)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ordered = new List<string>();
        void Add(string dir)
        {
            var full = Path.TrimEndingDirectorySeparator(dir);
            if (seen.Add(full) && IsFlavorDir(full)) ordered.Add(full);
        }
        foreach (var dir in candidates) Add(dir);
        foreach (var root in candidates.Select(d => Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(d))).OfType<string>()
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (var dir in FlavorsUnder(root)) Add(dir);
        }
        return [.. ordered.Where(d => new WowInstall(d).AltStableInstalled)];
    }

    /// <summary>The other flavour folders beside this one where AltStable is installed, by name.</summary>
    public static IReadOnlyList<string> AltStableElsewhere(WowInstall install)
    {
        try
        {
            return [.. FlavorsUnder(install.Root)
                .Where(d => !string.Equals(Path.TrimEndingDirectorySeparator(d), Path.TrimEndingDirectorySeparator(install.FlavorDir), StringComparison.OrdinalIgnoreCase))
                .Where(d => new WowInstall(d).AltStableInstalled)];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static IEnumerable<string> CandidateFlavorDirs()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var dir in Candidates())
        {
            var full = Path.TrimEndingDirectorySeparator(dir);
            if (seen.Add(full)) yield return full;
        }

        static IEnumerable<string> Candidates()
        {
            if (OperatingSystem.IsWindows())
            {
                if (Registry.LocalMachine.OpenSubKey(BlizzardKey + @"\Beta")?.GetValue("InstallPath") is string beta)
                {
                    yield return beta;
                }
                if (Registry.LocalMachine.OpenSubKey(BlizzardKey)?.GetValue("InstallPath") is string last
                    && Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(last)) is { } root)
                {
                    yield return Path.Combine(root, DefaultFlavor);
                }
            }
            yield return Path.Combine(DefaultRoot, DefaultFlavor);
        }
    }
}
