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

/// <summary>Enhanced pictures written since the player last said "Got it", newest last.</summary>
public sealed record EnhancedNote(DateTime At, IReadOnlyList<string> Names);

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
    PassNote? LastWritten = null,
    /// <summary>The player has read that portraits were written ("Got it").</summary>
    bool UpdateSeen = false,
    /// <summary>The last enhanced pictures of this install, beside - never instead of - the last pass that wrote.</summary>
    EnhancedNote? LastEnhanced = null,
    /// <summary>The player has read that pictures were enhanced ("Got it").</summary>
    bool EnhanceSeen = false,
    /// <summary>"Kaleid Sumner (2 of 5)" while a picture is being made; null otherwise.</summary>
    string? Enhancing = null,
    /// <summary>
    /// The very first start: nothing is converted until the player has read what happens to
    /// their screenshots and said "Start watching".
    /// </summary>
    bool FirstStart = false);

public enum HeadlineKind
{
    /// <summary>Nothing to do.</summary>
    Good,
    /// <summary>The app is doing something.</summary>
    Busy,
    /// <summary>Something to know, or a step to take in the game.</summary>
    Info,
    /// <summary>Something in the list needs the player.</summary>
    Attention,
    /// <summary>Something is wrong with the app's own work.</summary>
    Problem,
}

/// <summary>What the window leads with: how things stand, and the one next step that applies.</summary>
public sealed record Headline(string Title, string? Next, HeadlineKind Kind, bool Dismissable = false);

/// <summary>
/// What the app says: the headline, the next step, the list's lines, the balloon. Plain
/// strings from plain data, here so that they are tested like the rest.
/// </summary>
public static class PassText
{
    // Not "first portrait created": the notice is owed for a NEW ADDON FOLDER, which can also
    // be one that was repaired, and it stays until it is dismissed.
    public const string RestartNotice =
        "Restart WoW once to enable the portraits addon. WoW only finds a new addon folder when it starts: "
        + "if the game is open, quit it completely and start it again. After that, /reload is enough.";

    public const string FirstStart =
        "AltStable Companion turns the captures you take in game into portraits, by itself. "
        + "Once a portrait is written, the two screenshots it was made from are deleted. "
        + "No other screenshot is ever touched.";

    /// <summary>
    /// The words beside the enhancement setting: what it does, what leaves the PC, when it
    /// spends, what off does. Every clause is something the code does; nothing is promised
    /// beyond one attempt per combination.
    /// </summary>
    public static string EnhanceExplanation(int minLevel, string? codexStatus)
    {
        var signedIn = codexStatus is null || codexStatus == "installed" ? "the account the Codex CLI on this PC is signed in to"
            : $"the account the Codex CLI on this PC is signed in to ({codexStatus.Replace("Logged in using ", "")})";
        return $"When on, the Codex CLI on this PC is asked for a new picture of each character of level {minLevel} or more that has a portrait "
            + "and is not hidden - the existing ones too, all of them, when you turn this on. What is sent: the portrait's cropped image and "
            + $"the character's race, gender and class, plus any instructions your Codex CLI is configured with, to {signedIn}. "
            + "One automatic attempt for each new combination of capture, style and model; an attempt uses Codex usage whether or not a "
            + "picture comes back, and a picture that was refused or failed is not tried again for that combination. Turning this off "
            + "stops new attempts and cancels the one running (that one is tried once more when this is on again); pictures already "
            + "made stay, and the addon keeps showing them.";
    }

    /// <summary>Why the box is disabled, or null when it can be used.</summary>
    public static string? EnhanceUnavailable(bool codexFound, bool rosterCapable) =>
        !codexFound ? "The Codex CLI was not found on this PC." :
        !rosterCapable ? "The installed AltStable Roster cannot draw enhanced pictures yet: update the addon." : null;

    /// <summary>On a row whose picture could not be read this time. The portrait itself is not in question.</summary>
    public const string PreviewUnavailable = "preview unavailable: the file could not be read just now";

    /// <summary>The list, narrowed to the rows whose name has the text; all of them for none.</summary>
    public static IReadOnlyList<PortraitRow> Matching(IReadOnlyList<PortraitRow> rows, string? text)
    {
        var wanted = text?.Trim();
        if (string.IsNullOrEmpty(wanted)) return rows;
        return [.. rows.Where(r => r.Name.Contains(wanted, StringComparison.OrdinalIgnoreCase))];
    }

    /// <summary>Always in view once watching has started: the one thing the player does.</summary>
    public const string AddHint = "To add one: in WoW, /alts portrait, then /reload.";

    public static readonly IReadOnlyList<string> HowItWorks =
    [
        "1. In WoW: /alts portrait. The game takes two screenshots.",
        "2. /reload (or log out), so the game saves what it captured.",
        "3. This app makes the portrait, within a few seconds.",
        "4. /reload again to see it. The very first portrait needs the game restarted once.",
    ];

    /// <summary>
    /// How things stand and what to do next: the first of these that applies.
    ///
    /// The app can see files and capture records. It cannot see what the game has loaded, or a
    /// capture the game has not saved yet. So nothing here says "up to date", and what the
    /// player may have to do in the game is advice ("if WoW is open ..."), never a debt the
    /// app claims to have measured.
    /// </summary>
    public static Headline Headline(ShellState s) => Headline(s, restartNotice: false);

    /// <summary>
    /// The same, knowing whether the restart notice is owed: the one step that beats "/reload",
    /// because a reload does not find a new addon folder and the player would follow the
    /// headline, reload, and see nothing.
    /// </summary>
    public static Headline Headline(ShellState s, bool restartNotice)
    {
        if (s.Stopping) return new("Finishing...", null, HeadlineKind.Busy);
        if (s.Install is null)
        {
            return new("World of Warcraft folder not set",
                (s.InstallProblem ?? "Couldn't find the WoW folder") + ". Choose it in Settings.", HeadlineKind.Problem);
        }
        if (s.FirstStart) return new("Ready to start", null, HeadlineKind.Info);
        // A check that is running outranks the failure of the one before it: "something went
        // wrong" over a retry in progress reads as if the retry had failed already.
        if (s.Converting)
        {
            return new("Checking...", s.LastError is null ? null : "The check before this one failed.", HeadlineKind.Busy);
        }
        if (s.LastError is not null)
        {
            return new("The last check failed", s.Report is null
                ? "See the log, under Help."
                : "The list is from the last check that worked. See the log, under Help.", HeadlineKind.Problem);
        }
        if (s.Report is not { } report)
        {
            // Off is said once, by the marker under the headline, with the action beside it.
            return new("Not checked yet", null, s.Paused ? HeadlineKind.Info : HeadlineKind.Busy);
        }
        if (!report.ManifestWritten)
        {
            return new("Portraits written, but not listed",
                "The list the game reads could not be written. See the log, under Help.", HeadlineKind.Problem);
        }
        if (!s.Install.AltStableInstalled)
        {
            return new("AltStable is not installed in this game",
                "Install the AltStable addon, then start the game.", HeadlineKind.Problem);
        }
        if (report.Refused > 0)
        {
            return new("This app is too old for your AltStable", "Update AltStable Companion.", HeadlineKind.Problem);
        }
        // Both unread: the newer news first; "Got it" reads both.
        var enhanced = s.LastEnhanced is { Names.Count: > 0 } && !s.EnhanceSeen ? s.LastEnhanced : null;
        var written = s.LastWritten is { Written.Count: > 0 } && !s.UpdateSeen ? s.LastWritten : null;
        if (enhanced is not null && (written is null || enhanced.At >= written.At))
        {
            return new(enhanced.Names.Count == 1 ? $"Portrait enhanced: {Names(enhanced.Names)}" : $"{Names(enhanced.Names)} enhanced",
                restartNotice
                    ? "Restart WoW once to see " + (enhanced.Names.Count == 1 ? "it" : "them") + ": see below."
                    : "If WoW is open, /reload to load " + (enhanced.Names.Count == 1 ? "it." : "them."),
                HeadlineKind.Info, Dismissable: true);
        }
        if (s.LastWritten is { Written.Count: > 0 } wrote && !s.UpdateSeen)
        {
            return new(wrote.Written.Count == 1 ? $"Portrait written: {Names(wrote.Written)}" : $"{Names(wrote.Written)} written",
                restartNotice
                    ? "Restart WoW once to see " + (wrote.Written.Count == 1 ? "it" : "them") + ": see below."
                    : "If WoW is open, /reload to load " + (wrote.Written.Count == 1 ? "it." : "them."),
                HeadlineKind.Info, Dismissable: true);
        }
        var rows = report.Portraits ?? [];
        if (Attention(rows) is { } attention) return new(attention, "See the list.", HeadlineKind.Attention);
        if (report.Accounts == 0)
        {
            return new("No AltStable data yet", "Log in to WoW with the addon on, then /reload.", HeadlineKind.Info);
        }
        // Any screenshot newer than the records raises this, a hand-taken one too: it is a
        // hint about what MAY have happened, and the step is only for who did capture.
        if (report.Stale is not null)
        {
            return new("A capture may be waiting",
                "If you just captured a portrait, /reload in WoW so the game saves it.", HeadlineKind.Info);
        }
        if (rows.Count == 0)
        {
            return new("No portraits yet", "In WoW: /alts portrait, then /reload.", HeadlineKind.Info);
        }
        // A screenshot still on its way is not a capture that is converted, and not the
        // player's problem either: the next check looks again.
        if (rows.Any(r => r.Outcome == CaptureOutcome.Writing))
        {
            return new("A capture is still being written", "The next check looks at it again.", HeadlineKind.Info);
        }
        return new("Your portraits are ready",
            s.Paused ? null : "New captures are processed automatically while this app runs.", HeadlineKind.Good);
    }

    /// <summary>
    /// The number after "Your portraits ·": portrait files. A capture with no portrait is not
    /// a portrait, and a file two characters share is one portrait, not two.
    /// </summary>
    public static string Count(IReadOnlyList<PortraitRow> rows) =>
        rows.Where(r => r.Ready).Select(r => r.FileName).Distinct(StringComparer.OrdinalIgnoreCase).Count()
            .ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// "1 character needs attention", beside the count. Characters, not portraits: a character
    /// with no file at all is the usual one.
    /// </summary>
    public static string? Attention(IReadOnlyList<PortraitRow> rows) => rows.Count(r => r.NeedsAttention) switch
    {
        0 => null,
        1 => "1 character needs attention",
        var n => $"{n} characters need attention",
    };

    public static string RowState(PortraitRow row) => row.Ready ? "Ready" : "No portrait";

    /// <summary>
    /// The row's tooltip, from its <see cref="RowSummary"/> and <see cref="RowDetail"/>: the
    /// detail when the line is not already it, plus a word when the picture could not be read
    /// this time - and only that word when the line already says everything else. Null when
    /// there is nothing to add.
    /// </summary>
    public static string? RowTooltip(string? summary, string detail, bool previewFailed)
    {
        var more = summary is null ? null : detail;
        if (!previewFailed) return more;
        return more is null ? PreviewUnavailable : more + " · " + PreviewUnavailable;
    }

    /// <summary>
    /// The short line under a row's name, for a row with nothing to act on: only when - the
    /// capture's time for a portrait made from it, else the file's, each called what it is.
    /// The rest of <see cref="RowDetail"/> is for the tooltip. Null when the row has no short
    /// form: one that needs attention, one whose screenshot is still being written, or one
    /// that shares its name shows the full detail, because that line is the thing to read.
    /// </summary>
    public static string? RowSummary(PortraitRow row, DateTime today)
    {
        if (row.NeedsAttention || row.Outcome == CaptureOutcome.Writing || row.ShowGuid) return null;
        if (row.Outcome == CaptureOutcome.Converted && row.LatestCapture is { } captured) return "Captured " + When(captured, today);
        if (row.FileModified is { } modified) return "Updated " + When(modified, today);
        return "";
    }

    /// <summary>
    /// The line under a row's name. The portrait's time and the capture's time are said apart,
    /// each as what it is: a capture that was rejected did not update the portrait.
    /// </summary>
    public static string RowDetail(PortraitRow row, DateTime today)
    {
        var parts = new List<string>();
        if (row.Source == PortraitSource.File)
        {
            parts.Add(row.FileName ?? "");
            if (row.FileModified is { } modified) parts.Add("file modified " + When(modified, today));
        }
        else
        {
            if (row.LatestCapture is { } captured) parts.Add("latest capture " + When(captured, today));
            parts.Add(row.Outcome switch
            {
                CaptureOutcome.Converted => "converted",
                CaptureOutcome.Unknown => "portrait from an earlier converter",
                CaptureOutcome.NoScreenshots when row.Ready => "not converted: its screenshots are gone",
                CaptureOutcome.NoScreenshots => (row.Note ?? "no screenshots") + " - capture again if they are gone",
                CaptureOutcome.Writing => row.Note ?? "a screenshot is still being written",
                _ => row.Note ?? "not converted",
            });
            if (row.Source == PortraitSource.ByName) parts.Add($"portrait found by name ({row.FileName})");
            if (row.Ready && row.Outcome != CaptureOutcome.Converted && row.FileModified is { } modified)
            {
                parts.Add("file modified " + When(modified, today));
            }
        }
        if (row.Ready && row.NearlySquare && row.Outcome is CaptureOutcome.Converted or CaptureOutcome.Unknown)
        {
            parts.Add("nearly square: something else may be in it - capture again");
        }
        if (row.EnhanceNote is { } note) parts.Add(note);
        if (row.ShowGuid && row.Guid is not null) parts.Add(row.Guid);
        return string.Join(" · ", parts.Where(p => p.Length > 0));
    }

    // "today 12:13", "yesterday 09:05", else the date: for a row, where the minute is enough.
    private static string When(DateTime t, DateTime today) => Stamp(t, today, "HH:mm");

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
        if (s.Enhancing is { } who) return $"Enhancing {who}…" + (s.LastPass is null ? "" : " " + Activity(s with { Enhancing = null }, today));
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
            line += $" Last written: {Names(last.Written)}, {Time(last.At, today)}.";
        }
        return line;
    }

    private static string Names(IReadOnlyList<string> names) =>
        names.Count == 1 ? names[0] : $"{names.Count} portraits";

    // The same to the second, for the check line.
    private static string Time(DateTime t, DateTime today) => Stamp(t, today, "HH:mm:ss");

    // One rule for both: today and yesterday by name, any other day by its date.
    private static string Stamp(DateTime t, DateTime today, string clock)
    {
        var time = t.ToString(clock, System.Globalization.CultureInfo.InvariantCulture);
        if (t.Date == today.Date) return "today " + time;
        if (t.Date == today.Date.AddDays(-1)) return "yesterday " + time;
        return t.ToString("yyyy-MM-dd ", System.Globalization.CultureInfo.InvariantCulture) + time;
    }

    /// <summary>The tray icon's tooltip: the app's name and, in a word, what it is doing.</summary>
    public static string TrayTip(ShellState s)
    {
        var what = s switch
        {
            { Stopping: true } => "finishing",
            { Install: null } => "no WoW folder",
            { FirstStart: true } => "not started: open the window",
            { Converting: true } => "converting",
            { Paused: true } => "automatic processing off",
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

    /// <summary>
    /// The window's top line, in its three parts: which game, whether the addon is in it, and
    /// how many accounts have its data. Forever is the game this app is for and is called by
    /// its name; any other flavour is called by its folder.
    /// </summary>
    public static (string Game, string Addon, string? Accounts) InstallSummary(WowInstall install, PassReport? report)
    {
        var forever = string.Equals(install.Flavor, WowInstallLocator.DefaultFlavor, StringComparison.OrdinalIgnoreCase);
        return (
            forever ? "World of Warcraft Forever" : $"World of Warcraft ({install.Flavor})",
            install.AltStableInstalled ? "AltStable detected" : "AltStable not installed",
            report is null ? null : report.Accounts == 1 ? "1 account" : $"{report.Accounts} accounts");
    }

    /// <summary>The generated .toc says Interface 16001: only Forever reads it as current.</summary>
    public static string? FlavorWarning(WowInstall install) =>
        string.Equals(install.Flavor, WowInstallLocator.DefaultFlavor, StringComparison.OrdinalIgnoreCase)
            ? null
            : $"{install.Flavor} is not Forever ({WowInstallLocator.DefaultFlavor}): the game will mark the cutouts addon out of date.";
}
