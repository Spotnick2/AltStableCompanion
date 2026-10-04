namespace AltStableCompanion.Core;

/// <summary>
/// The command line.
/// <list type="bullet">
/// <item><c>--minimized</c>: start in the tray, no window.</item>
/// <item><c>--wow-dir &lt;folder&gt;</c>: use THIS flavour folder for the run, and no other.</item>
/// <item><c>--data-dir &lt;folder&gt;</c>: keep settings and the log there, not in %APPDATA%.</item>
/// </list>
/// Anything else is an error, and so is a folder that is not what it has to be. The app then
/// says so and stops. It never carries on with a guess: a mistyped <c>--wow-dir</c> that fell
/// back to detection would run a pass - which deletes screenshots - against the real install.
/// </summary>
public sealed record StartupOptions(bool Minimized = false, string? WowDir = null, string? DataDir = null)
{
    /// <summary>The player, or a developer, named the folders: this is not an ordinary start.</summary>
    public bool Explicit => WowDir is not null || DataDir is not null;

    /// <summary>The options, or what is wrong with the command line. Never both.</summary>
    public static (StartupOptions? Options, string? Error) Parse(IReadOnlyList<string> args)
    {
        var options = new StartupOptions();
        for (var i = 0; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "--minimized":
                    options = options with { Minimized = true };
                    break;
                case "--wow-dir" or "--data-dir":
                    if (i + 1 >= args.Count || args[i + 1].StartsWith("--", StringComparison.Ordinal)
                        || string.IsNullOrWhiteSpace(args[i + 1]))
                    {
                        return (null, $"{args[i]} needs a folder after it.");
                    }
                    string folder;
                    try
                    {
                        folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(args[i + 1]));
                    }
                    catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
                    {
                        return (null, $"{args[i]}: \"{args[i + 1]}\" is not a folder path.");
                    }
                    if (args[i] == "--wow-dir")
                    {
                        if (options.WowDir is not null) return (null, "--wow-dir is given twice.");
                        if (!WowInstallLocator.IsFlavorDir(folder))
                        {
                            return (null, $"--wow-dir: \"{folder}\" is not a World of Warcraft flavour folder "
                                + "(a folder such as _classic_beta_, holding Wow*.exe).");
                        }
                        options = options with { WowDir = folder };
                    }
                    else
                    {
                        if (options.DataDir is not null) return (null, "--data-dir is given twice.");
                        options = options with { DataDir = folder };
                    }
                    i++;
                    break;
                default:
                    return (null, $"\"{args[i]}\" is not an option this app knows. "
                        + "It knows --minimized, --wow-dir <folder> and --data-dir <folder>.");
            }
        }
        return (options, null);
    }

    /// <summary>
    /// Make sure <c>--data-dir</c> can be written to; what is wrong with it when it cannot.
    /// Apart from <see cref="Parse"/>, which changes nothing on disk: a folder is only created
    /// once the whole command line is known to be right and this is the instance that runs.
    /// </summary>
    public string? PrepareDataDir()
    {
        if (DataDir is null) return null;
        try
        {
            Directory.CreateDirectory(DataDir);
            var probe = Path.Combine(DataDir, ".write-test-" + Guid.NewGuid().ToString("N"));
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"--data-dir: \"{DataDir}\" cannot be used: {ex.Message}";
        }
    }
}

/// <summary>
/// Which install a run uses, or why it has none. <see cref="Detected"/>: found by looking,
/// not named by the player - the one case where another game with AltStable is worth a word.
/// </summary>
public sealed record ResolvedInstall(WowInstall? Install, string? Problem, bool Pinned, bool Detected = false)
{
    /// <summary>
    /// In strict order: the folder pinned on the command line; else the folder the player saved;
    /// else, and ONLY when neither was given, detection. A saved folder that has stopped being
    /// a flavour folder is a problem to show, not a reason to look elsewhere: silently picking
    /// another install could convert - and delete - in the wrong game.
    ///
    /// Settings that could not be READ (<paramref name="settingsProblem"/>) are not "no folder
    /// was saved". What the player chose is unknown, so nothing is chosen for them.
    /// </summary>
    public static ResolvedInstall Resolve(string? pinned, string? saved, Func<WowInstall?>? detect = null,
        string? settingsProblem = null)
    {
        if (pinned is not null)
        {
            return WowInstallLocator.IsFlavorDir(pinned)
                ? new ResolvedInstall(new WowInstall(pinned), null, Pinned: true)
                : new ResolvedInstall(null, $"The folder given with --wow-dir is not a WoW flavour folder: {pinned}", Pinned: true);
        }
        if (settingsProblem is not null)
        {
            return new ResolvedInstall(null, "The settings could not be read, so the WoW folder is not known", Pinned: false);
        }
        if (!string.IsNullOrWhiteSpace(saved))
        {
            return WowInstallLocator.IsFlavorDir(saved)
                ? new ResolvedInstall(new WowInstall(saved), null, Pinned: false)
                : new ResolvedInstall(null, $"The WoW folder chosen before is not there any more: {saved}", Pinned: false);
        }
        var found = (detect ?? WowInstallLocator.Detect)();
        return new ResolvedInstall(found, found is null ? "Couldn't find the WoW folder" : null, Pinned: false, Detected: found is not null);
    }

    /// <summary>
    /// What the player picked with Browse: a flavour folder, or a WoW folder holding Forever's.
    /// Null when it is neither - the current install then stays as it is.
    /// </summary>
    public static WowInstall? FromPicked(string folder)
    {
        folder = Path.TrimEndingDirectorySeparator(folder);
        if (WowInstallLocator.IsFlavorDir(folder)) return new WowInstall(folder);
        var forever = Path.Combine(folder, WowInstallLocator.DefaultFlavor);
        return WowInstallLocator.IsFlavorDir(forever) ? new WowInstall(forever) : null;
    }
}
