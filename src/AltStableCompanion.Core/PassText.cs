namespace AltStableCompanion.Core;

/// <summary>Everything the status line is worked out from. No UI type in it.</summary>
public sealed record ShellState(
    WowInstall? Install = null,
    string? InstallProblem = null,
    bool Paused = false,
    bool Converting = false,
    bool Stopping = false,
    string? LastError = null,
    PassReport? Report = null);

/// <summary>
/// What the app says about a pass: the status line, the balloon, a character's state. Plain
/// strings from plain data, here so that they are tested like the rest.
/// </summary>
public static class PassText
{
    public const string RestartNotice =
        "The AltStableCutouts addon was just created. WoW only notices a new addon folder at startup - "
        + "quit the game completely and start it again once. After that, Reload is enough.";

    /// <summary>
    /// One line, the most useful thing to know first. What the player has to act on outranks
    /// what the app is doing, and a hint outranks nothing:
    /// stopping, no install, converting, a failed pass, no addon, records this app cannot read,
    /// paused, no data, no pass yet, the reload hint, watching.
    /// </summary>
    public static string StatusLine(ShellState s)
    {
        if (s.Stopping) return "Finishing the pass in hand...";
        if (s.Install is null) return (s.InstallProblem ?? "Couldn't find the WoW folder") + " - Browse...";
        if (s.Converting) return s.Paused ? "Converting - watching is paused" : "Converting...";
        if (s.LastError is not null) return "The last pass failed - see the log";
        if (!s.Install.AltStableInstalled) return "AltStable is not installed in this flavour";
        if (s.Report is { Refused: > 0 })
        {
            return "Some capture records are newer than this app reads - update the companion";
        }
        if (s.Paused) return s.Report is null ? "Paused - nothing has been looked at yet" : "Paused";
        if (s.Report is null) return "Starting...";
        if (s.Report.Accounts == 0) return "No AltStable data yet - log in with the addon on, then Reload";
        // Any screenshot newer than the records counts, a hand-taken one too: this is a hint
        // about what MAY have happened, and is worded as one.
        if (s.Report.Stale is not null)
        {
            return "Newer screenshots found. If you just captured a portrait, Reload in game.";
        }
        return s.Report.Characters.Count == 0
            ? "Watching - in game: /alts portrait, then Reload"
            : "Watching";
    }

    /// <summary>The tray icon's tooltip: the app's name and, in a word, what it is doing.</summary>
    public static string TrayTip(ShellState s)
    {
        var what = s switch
        {
            { Stopping: true } => "finishing",
            { Install: null } => "no WoW folder",
            { Converting: true } => "converting",
            { Paused: true } => "paused",
            _ => "watching",
        };
        return $"AltStable Companion - {what}";
    }

    /// <summary>
    /// The balloon for a pass, or null when it wrote nothing. Rejections and failures never
    /// raise one: the list in the window is where they are read.
    /// </summary>
    public static (string Title, string Text)? Balloon(PassReport report)
    {
        if (report.Written.Count == 0) return null;
        var what = report.Written.Count == 1
            ? $"Portrait written: {report.Written[0]}"
            : $"{report.Written.Count} portraits written";
        if (report.FolderCreated)
        {
            return (what, "WoW only notices a new addon folder at startup: quit the game completely and start it again once.");
        }
        return (what, report.Written.Count == 1 ? "Reload in game to see it." : "Reload in game to see them.");
    }

    public static string StateLabel(CharacterState state) => state switch
    {
        CharacterState.Portrait => "Portrait",
        CharacterState.Missing => "No screenshots",
        CharacterState.Ambiguous => "Left alone",
        CharacterState.Collided => "Capture again",
        CharacterState.Rejected => "Not a portrait",
        CharacterState.Failed => "Failed",
        _ => state.ToString(),
    };

    /// <summary>The line under the path: what was found in the install.</summary>
    public static string InstallDetail(WowInstall install, PassReport? report)
    {
        var addon = install.AltStableInstalled ? "AltStable: installed" : "AltStable: not installed";
        if (report is null) return addon;
        var accounts = report.Accounts == 1 ? "1 account" : $"{report.Accounts} accounts";
        return $"{addon} - {accounts}";
    }

    /// <summary>The generated .toc says Interface 16001: only Forever reads it as current.</summary>
    public static string? FlavorWarning(WowInstall install) =>
        string.Equals(install.Flavor, WowInstallLocator.DefaultFlavor, StringComparison.OrdinalIgnoreCase)
            ? null
            : $"{install.Flavor} is not Forever ({WowInstallLocator.DefaultFlavor}): the game will mark the cutouts addon out of date.";
}
