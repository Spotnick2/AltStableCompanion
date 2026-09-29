namespace AltStableCompanion.Core;

/// <summary>
/// What one pass did, written down when it ended. Its own record, and not read back out of
/// the last report: a pass that throws has no report, and the one before it is still there.
/// </summary>
/// <param name="At">When it ended.</param>
/// <param name="Written">The portraits it wrote.</param>
/// <param name="Unconverted">Captures it looked at and could not use: collided, rejected,
///   ambiguous, or failed in the writing. They are in the list, each with its reason.</param>
/// <param name="Waiting">Screenshots newer than any record: a capture may be waiting for a Reload.</param>
/// <param name="Failed">The pass itself threw.</param>
public sealed record PassNote(DateTime At, IReadOnlyList<string> Written, int Unconverted = 0,
    bool Waiting = false, bool Failed = false)
{
    public static PassNote Of(PassReport? report, DateTime at) => report is null
        ? new PassNote(at, [], Failed: true)
        : new PassNote(at, report.Written,
            report.Characters.Count(c => c.State is CharacterState.Collided or CharacterState.Rejected
                or CharacterState.Ambiguous or CharacterState.Failed),
            Waiting: report.Stale is not null);
}

/// <summary>Everything the status line is worked out from. No UI type in it.</summary>
public sealed record ShellState(
    WowInstall? Install = null,
    string? InstallProblem = null,
    bool Paused = false,
    bool Converting = false,
    bool Stopping = false,
    string? LastError = null,
    PassReport? Report = null,
    /// <summary>The last pass of this install, whatever it found.</summary>
    PassNote? LastPass = null,
    /// <summary>The last pass of this install that wrote a portrait, since the app started.</summary>
    PassNote? LastWritten = null);

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
        // "Watching" is where the app rests: a capture has been dealt with and it is waiting
        // for the next. What it last did is said beside it, by Activity.
        return s.Report.Characters.Count == 0
            ? "Watching for captures - in game: /alts portrait, then Reload"
            : "Watching for new captures";
    }

    /// <summary>
    /// What the last pass did, and when - so that a pass which found nothing still shows that
    /// it ran ("Convert now" with nothing to convert looked like a button that does nothing),
    /// and so that the last portrait written stays in view after later passes find nothing.
    /// Null until a pass has run.
    ///
    /// "Nothing new" is only said of a pass that had nothing to look at. One that looked at a
    /// capture and could not use it says so, and so does one with screenshots no record owns.
    /// A time that is not of <paramref name="today"/> carries its date: the app stays in the
    /// tray for days.
    /// </summary>
    public static string? Activity(ShellState s, DateTime today)
    {
        if (s.LastPass is not { } pass) return null;
        var attention = pass.Unconverted switch
        {
            0 => null,
            1 => "1 capture could not be converted - see the list",
            var n => $"{n} captures could not be converted - see the list",
        };
        var what = pass.Failed ? "the pass failed"
            : pass.Written.Count > 0 ? "wrote " + Names(pass.Written) + (attention is null ? "" : "; " + attention)
            : attention is not null ? "nothing written; " + attention
            : pass.Waiting ? "nothing to convert yet"
            : "nothing new";
        var line = $"Last check {Time(pass.At, today)}: {what}.";
        if (pass.Written.Count == 0 && s.LastWritten is { Written.Count: > 0 } last)
        {
            line += $" Last written: {Names(last.Written)}, at {Time(last.At, today)}.";
        }
        return line;
    }

    private static string Names(IReadOnlyList<string> names) =>
        names.Count == 1 ? names[0] : $"{names.Count} portraits";

    private static string Time(DateTime t, DateTime today) => t.ToString(
        t.Date == today.Date ? "HH:mm:ss" : "yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);

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
            ? $"Portrait written: {Names(report.Written)}"
            : $"{Names(report.Written)} written";
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
