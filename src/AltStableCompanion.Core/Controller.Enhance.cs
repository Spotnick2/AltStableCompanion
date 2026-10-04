namespace AltStableCompanion.Core;

/// <summary>
/// How the controller reaches Codex: replaceable as a whole for the tests, which hand in a
/// shim, and there is no path from a shim back to the real one. <see cref="Generate"/> is
/// handed the job and the job folder (with the reference in it) and returns the picture or
/// why not; <see cref="CodexFound"/> says whether the CLI is there, and how it is signed in.
/// </summary>
public sealed record EnhanceHooks(
    Func<CodexJob, string, CancellationToken, Task<CodexResult>> Generate,
    Func<CancellationToken, Task<string?>> CodexFound)
{
    /// <summary>
    /// The real thing: the CLI on PATH, its own home, its login status - looked for at each
    /// call, since the player may install Codex, or remove it, while the app runs.
    /// </summary>
    public static EnhanceHooks Real() => new(
        (job, dir, ct) => CodexImageGen.Find() is { } exe
            ? new CodexImageGen(exe, CodexImageGen.DefaultHome()).GenerateAsync(job, dir, ct)
            : Task.FromResult(new CodexResult(null, "codex is not on PATH", "")),
        async ct => CodexImageGen.Find() is { } exe ? await CodexImageGen.LoginStatusAsync(exe, ct) ?? "installed" : null);
}

public sealed partial class Controller
{
    /// <summary>A generation ended - written, refused, failed or cancelled - on whatever thread it ended on.</summary>
    public event Action<EnhanceResult>? EnhanceCompleted;

    private readonly SemaphoreSlim _enhanceWake = new(0, 1);
    // The generation in flight, if any. Never disposed: whoever cancels it (the window, quit,
    // Browse) does so outside the gate, after the worker may have let go of it.
    private CancellationTokenSource? _jobCts;
    private Task? _enhanceLoop;
    private string? _codexStatus;                            // null: not found; else how it is signed in
    private bool _codexProbed;
    private DateTime _codexProbedAt;                         // UTC; looked for again after ProbeAgain, or when asked
    private bool _reprobeCodex;
    private int _sweptGeneration = -1;                       // the install whose open records were closed
    private string? _enhanceRefused;                         // why nobody is eligible right now, logged once
    // The pictures the player asked for again: the SIGNATURES that were held at the click - a
    // character, a capture, a combination - and nothing else. Kept in the data dir, so a batch
    // the app did not live to finish goes on after a restart; a signature leaves the set once
    // it is in the history. No settings change can widen it: another combination is another
    // signature, held until asked.
    private readonly HashSet<string> _remakeAllowed = new(StringComparer.Ordinal);
    private IReadOnlyList<(string Name, string Signature)> _held = [];   // what the window offers, as of the last look
    private Placement? _batch;                                           // this wake's placement, reused across its launches

    /// <summary>One wake's placement of the candidates: who is to launch, under which settings.</summary>
    private sealed record Placement((string Style, string Model, string Effort, int Level) Settings, int Generation,
        List<(EnhanceCandidate C, CutoutMeta Meta, string Primary, string Signature)> Launch, int Done)
    {
        public int Launched;
    }

    private string RemakePath => Path.Combine(_dataDir, "remake.json");

    private void LoadRemake()
    {
        try
        {
            if (!File.Exists(RemakePath)) return;
            var list = System.Text.Json.JsonSerializer.Deserialize<List<string>>(File.ReadAllText(RemakePath));
            lock (_gate) { foreach (var sig in list ?? []) if (!string.IsNullOrWhiteSpace(sig)) _remakeAllowed.Add(sig); }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            _log?.Write($"enhance: {RemakePath} could not be read: {ex.Message}");
        }
    }

    // Under _gate.
    private void SaveRemake()
    {
        try
        {
            if (_remakeAllowed.Count == 0) { if (File.Exists(RemakePath)) File.Delete(RemakePath); return; }
            Directory.CreateDirectory(_dataDir);
            File.WriteAllText(RemakePath, System.Text.Json.JsonSerializer.Serialize(_remakeAllowed.Order(StringComparer.Ordinal).ToList()));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log?.Write($"enhance: {RemakePath} could not be written: {ex.Message}");
        }
    }

    /// <summary>How long the CLI's presence and sign-in are taken on trust while enhancing.</summary>
    public static readonly TimeSpan ProbeAgain = TimeSpan.FromMinutes(5);

    /// <summary>What one launch carries from its preparation to its publication.</summary>
    private sealed record EnhanceJob(string Guid, string Base, string Name, string Signature, string SourceHash,
        CutoutMeta PrimaryMeta, string Style, string Dir, CodexJob Codex, int Generation, string InstallDir);

    /// <summary>The worker is nudged: something that decides who is eligible changed.</summary>
    private void WakeEnhancer()
    {
        try { _enhanceWake.Release(); } catch (SemaphoreFullException) { }
    }

    private void StartEnhancer()
    {
        if (_enhance is null) return;
        // Job folders are this run's; whatever a run that did not end left is a copy of a
        // portrait nobody needs.
        TryDeleteDir(Path.Combine(_dataDir, "enhance"));
        LoadRemake();
        _enhanceLoop = Task.Run(EnhanceLoopAsync);
        WakeEnhancer();
    }

    private async Task EnhanceLoopAsync()
    {
        while (true)
        {
            try
            {
                await _enhanceWake.WaitAsync();
                lock (_gate) { if (_stopping) return; }
                await ProbeCodexAsync();
                _batch = null;   // a wake looks at everyone again; the launches of one wake share that look
                while (true)
                {
                    var job = PrepareEnhancement();
                    if (job is null) break;
                    CancellationTokenSource cts;
                    // The launch, and the last look before it: the setting, the style and the
                    // install may have changed while the job was prepared.
                    string? abandon;
                    lock (_gate)
                    {
                        abandon = _stopping ? "the app is stopping"
                            : !_settings.Enhance ? "turned off before the launch"
                            : _settings.EnhanceStyle != job.Style ? "the style changed before the launch"
                            : job.Generation != _generation ? "the game changed before the launch"
                            : null;
                        cts = abandon is null ? _jobCts = new CancellationTokenSource() : new CancellationTokenSource();
                    }
                    if (abandon is not null)
                    {
                        AbandonJob(job, abandon);
                        if (_stopping) return;
                        continue;
                    }
                    CodexResult result;
                    try
                    {
                        result = await _enhance!.Generate(job.Codex, job.Dir, cts.Token);
                    }
                    catch (Exception ex)
                    {
                        result = new CodexResult(null, "the generation threw: " + ex.Message, "");
                    }
                    lock (_gate) { if (ReferenceEquals(_jobCts, cts)) _jobCts = null; }
                    PublishEnhancement(job, result);
                }
            }
            catch (Exception ex)
            {
                _log?.Write($"the enhancer stopped on an error and will try again on the next wake: {ex}");
            }
        }
    }

    // Once at the start, so the window is right before the box is ticked; then, while
    // enhancing, when the window asked (the setting changed) or after a while.
    private async Task ProbeCodexAsync()
    {
        bool probe;
        lock (_gate)
        {
            probe = !_codexProbed || (_settings.Enhance && (_reprobeCodex || DateTime.UtcNow - _codexProbedAt > ProbeAgain));
            _reprobeCodex = false;
        }
        if (!probe) return;
        var status = await _enhance!.CodexFound(CancellationToken.None);
        lock (_gate)
        {
            _codexProbed = true;
            _codexProbedAt = DateTime.UtcNow;
            _codexStatus = status;
            _current = _current with { CodexStatus = status, CodexProbed = true };
        }
        Changed?.Invoke();
    }

    /// <summary>
    /// Under the pass gate, then the state gate, as a pass is: the next character to spend a
    /// generation on, its attempt recorded BEFORE this returns, or null when there is none.
    /// </summary>
    private EnhanceJob? PrepareEnhancement()
    {
        lock (_passGate)
        {
            Settings settings;
            WowInstall install;
            int generation;
            string? codex;
            bool sweep;
            ShellState shell;
            lock (_gate)
            {
                if (_stopping || _current.Shell.Install is null) return Idle(null);
                settings = _settings;
                shell = _current.Shell;
                install = shell.Install!;
                generation = _generation;
                codex = _codexStatus;
                sweep = _sweptGeneration != generation;
                _sweptGeneration = generation;
            }
            // Whatever is still open in this install's history was begun by a run that did not
            // live to end it: this loop is the only launcher, and nothing of its is in flight
            // here. Closed, the row stops saying "enhancing"; the signature stays attempted.
            if (sweep)
            {
                var swept = new CutoutFolder(install.CutoutAddonDir);
                var closed = AttemptHistory.CloseOpen(swept.EnhancedDir,
                    Attempt.Cancelled + ": the app did not live to finish it", _clock(), m => _log?.Write("enhance: " + m));
                if (closed > 0)
                {
                    _log?.Write($"enhance: {closed} attempt(s) left open by an earlier run closed");
                    RefreshAfterEnhancement(swept, generation, null);
                }
            }
            if (!settings.Enhance || shell.FirstStart || shell.Paused) return Idle(null);
            if (codex is null) return Idle("the Codex CLI is not on this PC");
            if (!install.RosterDrawsEnhanced) return Idle("the installed AltStable Roster cannot draw enhanced pictures");

            var folder = new CutoutFolder(install.CutoutAddonDir);
            if (!folder.Exists) return Idle("no portraits yet");
            var batch = _batch;
            var under = (settings.EnhanceStyle, settings.EnhanceModel, settings.EnhanceEffort, settings.EnhanceMinLevel);
            // A wake that arrived during the last generation - a pass after a roster or file
            // change, a setting - means the look is stale: a character hidden meanwhile, or an
            // account that can no longer be read, must not be sent from the old list.
            if (_enhanceWake.CurrentCount > 0) batch = null;
            if (batch is null || batch.Settings != under || batch.Generation != generation)
            {
                // The look: everyone placed - to launch, held, or done - once per wake, so the
                // held are offered together and the batch does not hash the roster for every launch.
                batch = _batch = Place(folder, install, settings, generation, under);
                if (batch is null) return null;
            }

            while (batch.Launched < batch.Launch.Count)
            {
                var (c, placedMeta, primary, placed) = batch.Launch[batch.Launched++];
                // The placement is a wake old, and a wake comes two seconds after the roster
                // changed on disk: the roster is read again for the one being launched. Hidden
                // since, below the level, another portrait, another race or class - or an
                // account that can no longer be read, which refuses everyone - and the look is
                // done again rather than spending on the old one.
                var fresh = Eligibility.Select(SavedVariablesReader.Snapshot(install.AccountsDir, m => _log?.Write(m)),
                    settings.EnhanceMinLevel, guid => folder.FileBaseOf(guid));
                if (fresh.Refused is { } refusedNow) { _batch = null; return Idle(refusedNow); }
                var still = fresh.Candidates.FirstOrDefault(e => e.Guid == c.Guid);
                if (still is null || still.Character != c.Character || still.FileBase != c.FileBase)
                {
                    _batch = null;
                    return PrepareEnhancement();
                }
                // The portrait, its sidecar and the history, likewise: a capture since (the same
                // pixels with a new epoch included) or a record since would make it another picture.
                var meta = folder.ReadMeta(c.FileBase);
                if (meta is null || meta.Guid != c.Guid || meta.Epoch != placedMeta.Epoch || meta.W != placedMeta.W || meta.H != placedMeta.H)
                {
                    _batch = null;
                    return PrepareEnhancement();
                }
                string sourceHash;
                RgbaImage canvas;
                try
                {
                    sourceHash = EnhancementSignature.HashOf(primary);
                    canvas = TgaCodec.Read(primary);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TgaFormatException)
                {
                    _log?.Write($"enhance: {c.FileBase}.tga could not be read this time: {ex.Message}");
                    continue;
                }
                var signature = EnhancementSignature.Compute(c.Guid, meta.Epoch, sourceHash, c.Character,
                    settings.EnhanceStyle, settings.EnhanceModel, settings.EnhanceEffort);
                if (signature != placed) { _batch = null; return PrepareEnhancement(); }
                AttemptHistory history;
                try
                {
                    history = AttemptHistory.Load(folder.EnhancedDir, c.Guid);
                }
                catch (AttemptHistoryException ex)
                {
                    _log?.Write($"enhance: {c.Character.Name} skipped: {ex.Message}");
                    continue;
                }
                if (!history.MayLaunch(signature)) continue;

                // The job folder: the reference (the manifest's crop of the primary, as a PNG),
                // and later the verdict. Ours, deleted when the job ends.
                var dir = Path.Combine(_dataDir, "enhance", Guid.NewGuid().ToString("N"));
                try
                {
                    Directory.CreateDirectory(dir);
                    var w = Math.Clamp(meta.W, 1, canvas.Width);
                    var h = Math.Clamp(meta.H, 1, canvas.Height);
                    PngCodec.Write(Path.Combine(dir, "reference.png"), canvas.Crop(0, 0, w, h));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _log?.Write($"enhance: the job folder could not be made: {ex.Message}");
                    return Idle("the job folder could not be made - see the log");
                }
                // The record, before the launch: whatever happens from here, this signature was attempted.
                try
                {
                    history.Begin(new Attempt
                    {
                        Signature = signature, Base = c.FileBase, Started = _clock(),
                        Style = settings.EnhanceStyle, Model = settings.EnhanceModel, Effort = settings.EnhanceEffort,
                        Prompt = EnhancementPrompt.Version, Epoch = meta.Epoch, SourceHash = sourceHash,
                    });
                }
                catch (AttemptHistoryException ex)
                {
                    _log?.Write($"enhance: {c.Character.Name} not launched: {ex.Message}");
                    TryDeleteDir(dir);
                    continue;
                }
                var codexJob = new CodexJob(EnhancementPrompt.Build(c.Character, settings.EnhanceStyle),
                    Path.Combine(dir, "reference.png"), settings.EnhanceModel, settings.EnhanceEffort,
                    TimeSpan.FromSeconds(settings.EnhanceTimeoutSeconds));
                lock (_gate)
                {
                    _enhanceRefused = null;
                    // Counted through the batch, the ones already made included: "B (2 of 3)".
                    _current = _current with { Shell = _current.Shell with { Enhancing = $"{c.Character.Name} ({batch.Done + batch.Launched} of {batch.Done + batch.Launch.Count})" } };
                }
                // The row says "Enhancing" from now: the record is open.
                RefreshAfterEnhancement(folder, generation, null);
                Changed?.Invoke();
                _log?.Write($"enhance: {c.Character.Name} ({c.FileBase}), {settings.EnhanceStyle}, {settings.EnhanceModel} {settings.EnhanceEffort}");
                return new EnhanceJob(c.Guid, c.FileBase, c.Character.Name, signature, sourceHash, meta, settings.EnhanceStyle,
                    dir, codexJob, generation, install.CutoutAddonDir);
            }
            return Idle(null, keepHeld: true);
        }
    }

    // Under _passGate. Everyone eligible, placed: to launch (in roster order), held (offered to
    // the window, by name, sorted), or done. Null when the roster refuses.
    private Placement? Place(CutoutFolder folder, WowInstall install, Settings settings, int generation,
        (string Style, string Model, string Effort, int Level) under)
    {
        var snapshot = SavedVariablesReader.Snapshot(install.AccountsDir, m => _log?.Write(m));
        var eligible = Eligibility.Select(snapshot, settings.EnhanceMinLevel, guid => folder.FileBaseOf(guid));
        if (eligible.Refused is { } refused) { Idle(refused); return null; }

        HashSet<string> allowed;
        lock (_gate) { allowed = [.. _remakeAllowed]; }
        var held = new List<(string Name, string Signature)>();
        var launch = new List<(EnhanceCandidate C, CutoutMeta Meta, string Primary, string Signature)>();
        var done = 0;
        var inHistory = new List<string>();
        foreach (var c in eligible.Candidates)
        {
            var primary = Path.Combine(folder.CutoutsDir, c.FileBase + ".tga");
            var meta = folder.ReadMeta(c.FileBase);
            if (meta?.Guid != c.Guid || !File.Exists(primary)) continue;
            string sourceHash;
            try
            {
                sourceHash = EnhancementSignature.HashOf(primary);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log?.Write($"enhance: {c.FileBase}.tga could not be read this time: {ex.Message}");
                continue;
            }
            var signature = EnhancementSignature.Compute(c.Guid, meta.Epoch, sourceHash, c.Character,
                settings.EnhanceStyle, settings.EnhanceModel, settings.EnhanceEffort);
            AttemptHistory history;
            try
            {
                history = AttemptHistory.Load(folder.EnhancedDir, c.Guid);
            }
            catch (AttemptHistoryException ex)
            {
                _log?.Write($"enhance: {c.Character.Name} skipped: {ex.Message}");
                continue;
            }
            if (!history.MayLaunch(signature))
            {
                done++;
                if (allowed.Contains(signature)) inHistory.Add(signature);
                continue;
            }
            // A picture made from THIS portrait, with other settings or wording, stays until the
            // player asks for it: a style change or an app update is not a decision to spend. A
            // new capture (another epoch or other bytes) or no picture at all is.
            var made = history.LastWritten;
            if (!allowed.Contains(signature) && made is not null && made.Epoch == meta.Epoch && made.SourceHash == sourceHash
                && File.Exists(Path.Combine(folder.EnhancedDir, c.FileBase + ".tga")))
            {
                held.Add((c.Character.Name, signature));
                continue;
            }
            launch.Add((c, meta, primary, signature));
        }
        held.Sort((x, y) => string.Compare(x.Name, y.Name, StringComparison.OrdinalIgnoreCase));
        var names = held.Select(h => h.Name).ToList();
        var changed = false;
        lock (_gate)
        {
            _held = held;
            // A permission that is in the history has been used: the set shrinks to nothing, and the file with it.
            if (inHistory.Count > 0) { _remakeAllowed.ExceptWith(inHistory); SaveRemake(); }
            if (!(_current.Shell.EnhanceHeld ?? []).SequenceEqual(names))
            {
                _current = _current with { Shell = _current.Shell with { EnhanceHeld = names.Count == 0 ? null : names } };
                changed = true;
            }
        }
        if (changed) Changed?.Invoke();
        return new Placement(under, generation, launch, done);
    }

    // Nothing to do, and why - said once in the log when it is a reason. Before the look, the
    // hold is cleared: the names the window offers are the last look's, and there was none.
    private EnhanceJob? Idle(string? because, bool keepHeld = false)
    {
        var changed = false;
        lock (_gate)
        {
            if (because is not null && _enhanceRefused != because) { _log?.Write("enhance: " + because); _enhanceRefused = because; }
            if (_current.Shell.Enhancing is not null)
            {
                _current = _current with { Shell = _current.Shell with { Enhancing = null } };
                changed = true;
            }
            if (!keepHeld && (_current.Shell.EnhanceHeld is not null || _held.Count > 0))
            {
                _held = [];
                _current = _current with { Shell = _current.Shell with { EnhanceHeld = null } };
                changed = true;
            }
        }
        if (changed) Changed?.Invoke();
        return null;
    }

    // The job never ran: the record it left says so.
    private void AbandonJob(EnhanceJob job, string why)
    {
        var outcome = Attempt.Cancelled + ": " + why;
        var folder = new CutoutFolder(job.InstallDir);
        lock (_passGate)
        {
            Record(folder, job, outcome, null);
            RefreshAfterEnhancement(folder, job.Generation, null);
        }
        TryDeleteDir(job.Dir);
        Ended(job, outcome);
    }

    // How it ended, for the status line, the tooltip and the balloon.
    private void Ended(EnhanceJob job, string outcome)
    {
        var result = new EnhanceResult(_clock(), job.Name, outcome);
        lock (_gate)
        {
            // The activity ends with the job, not with the next look for one.
            _current = _current with { Shell = _current.Shell with { Enhancing = null, LastEnhanceResult = job.Generation == _generation ? result : _current.Shell.LastEnhanceResult } };
        }
        Changed?.Invoke();
        EnhanceCompleted?.Invoke(result);
    }

    /// <summary>
    /// What came back, decoded and checked OUTSIDE the gates; then, under them, the record and
    /// the files, in that order - the record first, so a crash between the two leaves an
    /// attempt that says "written" (the signature stays attempted, the player sees no picture
    /// and can delete the record to try again). A picture that came back complete is written
    /// into the install it was made for, whatever changed meanwhile: the usage is spent, and
    /// the portrait it was made from is the one check that matters.
    /// </summary>
    private void PublishEnhancement(EnhanceJob job, CodexResult result)
    {
        string outcome;
        Cutout? cutout = null;
        string? outputHash = null;
        if (!result.Ok)
        {
            outcome = (result.Failure == "cancelled" ? Attempt.Cancelled : Attempt.Failed + ": " + result.Failure);
            if (result.Detail.Length > 0) _log?.Write($"enhance: {job.Name}: {result.Failure}\n{result.Detail}");
        }
        else
        {
            try
            {
                var png = PngCodec.Read(result.Png!);
                cutout = Enhancement.ToCutout(png, job.PrimaryMeta with
                {
                    Shots = null,
                    Enhancement = new EnhancementMeta
                    {
                        SourceHash = job.SourceHash, Style = job.Style, Model = job.Codex.Model,
                        Effort = job.Codex.Effort, Prompt = EnhancementPrompt.Version, Signature = job.Signature,
                        Generated = DateTime.UtcNow,
                    },
                });
                var ms = new MemoryStream();
                TgaCodec.Write(ms, cutout.Canvas);
                outputHash = EnhancementSignature.HashOf(ms.ToArray());
                cutout = cutout with { Meta = cutout.Meta with { Enhancement = cutout.Meta.Enhancement! with { OutputHash = outputHash } } };
                outcome = Attempt.Written;
            }
            catch (PngFormatException ex)
            {
                outcome = Attempt.Failed + ": " + ex.Message;
            }
            catch (ThumbnailException ex)
            {
                outcome = Attempt.Refused + ": " + ex.Message;
            }
        }

        lock (_passGate)
        {
            var folder = new CutoutFolder(job.InstallDir);
            if (outcome == Attempt.Written)
            {
                // The portrait it was made from must still be the portrait: a new capture
                // since means a new signature, and this picture is nobody's.
                string now;
                try { now = EnhancementSignature.HashOf(Path.Combine(folder.CutoutsDir, job.Base + ".tga")); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { now = ""; }
                if (now != job.SourceHash) outcome = Attempt.Failed + ": the portrait changed while the picture was made";
            }
            Record(folder, job, outcome, outputHash);
            if (outcome == Attempt.Written)
            {
                try
                {
                    folder.WriteEnhanced(job.Base, cutout!.Canvas, cutout.Meta);
                    _log?.Write($"enhance: {job.Name} written ({cutout.Meta.W}x{cutout.Meta.H})");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _log?.Write($"enhance: {job.Name}: the picture could not be written: {ex.Message}");
                    // The record said written; it must not stay saying so.
                    outcome = Attempt.Failed + ": could not be written - " + ex.Message;
                    Record(folder, job, outcome, null);
                }
            }
            else
            {
                _log?.Write($"enhance: {job.Name}: {outcome}");
            }
            RefreshAfterEnhancement(folder, job.Generation, outcome == Attempt.Written ? job.Name : null);
        }
        TryDeleteDir(job.Dir);
        Ended(job, outcome);
    }

    private void Record(CutoutFolder folder, EnhanceJob job, string outcome, string? outputHash)
    {
        try
        {
            var history = AttemptHistory.Load(folder.EnhancedDir, job.Guid);
            history.End(job.Signature, outcome, _clock(), outputHash);
            // A permission is used up once the history refuses the signature (made, refused,
            // failed, or cancelled twice); a cancel the history would try once more keeps it.
            if (!history.MayLaunch(job.Signature))
            {
                lock (_gate) { if (_remakeAllowed.Remove(job.Signature)) SaveRemake(); }
            }
        }
        catch (AttemptHistoryException ex)
        {
            _log?.Write($"enhance: {ex.Message}");
        }
    }

    // Under _passGate. The manifest and the rows again, from the folder as it is now; the
    // notice, when a picture was written - beside the last pass's, never instead of it.
    private void RefreshAfterEnhancement(CutoutFolder folder, int generation, string? written)
    {
        IReadOnlyList<ManifestEntry> entries;
        IReadOnlyDictionary<string, DateTime> times;
        try
        {
            // The pass of this install remembers what the last listing found: it decides what
            // is new, so the log says a warning when it appears and not at every look.
            ConvertPass? pass;
            lock (_gate) pass = generation == _generation ? _pass : null;
            var listed = new List<string>();
            entries = folder.Inventory(listed.Add);
            if (pass is not null) pass.Listed(listed);
            else foreach (var m in listed) _log?.Write(m);
            times = folder.FileTimes(entries);
            folder.WriteManifest(entries, _clock());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log?.Write($"enhance: the manifest could not be written: {ex.Message}");
            return;
        }
        lock (_gate)
        {
            if (generation != _generation || _current.Shell.Report is not { } report) return;
            var rows = Collection.Build(entries, report.Characters, times, folder.EnhanceState);
            var shell = _current.Shell;
            EnhancedNote? note = null;
            if (written is not null)
            {
                // Unread news gathers: "Kaleid Sumner, Zoruka enhanced".
                var unread = shell.LastEnhanced is { } earlier && !shell.EnhanceSeen ? earlier.Names : [];
                note = new EnhancedNote(_clock(), [.. unread, written]);
            }
            _current = _current with
            {
                Shell = shell with
                {
                    Report = report with { Portraits = rows },
                    LastEnhanced = note ?? shell.LastEnhanced,
                    EnhanceSeen = note is null && shell.EnhanceSeen,
                },
            };
        }
    }

    private void TryDeleteDir(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _log?.Write($"enhance: {dir} could not be deleted: {ex.Message}"); }
    }

    /// <summary>
    /// The enhancement settings, from the window. Turning it off, or changing the style,
    /// cancels the picture in flight. The setting changes under the gate BEFORE the
    /// cancellation, so a job prepared in the meantime finds it off at its launch.
    /// </summary>
    public void SetEnhance(bool on, int minLevel, string style)
    {
        CancellationTokenSource? cancel = null;
        lock (_gate)
        {
            var before = (_settings.Enhance, _settings.EnhanceMinLevel, _settings.EnhanceStyle);
            var after = new Settings { Enhance = on, EnhanceMinLevel = minLevel, EnhanceStyle = style };
            if (before == (after.Enhance, after.EnhanceMinLevel, after.EnhanceStyle)) return;
            _settings = _settings with { Enhance = after.Enhance, EnhanceMinLevel = after.EnhanceMinLevel, EnhanceStyle = after.EnhanceStyle };
            Write();
            if (!on || before.EnhanceStyle != after.EnhanceStyle) { cancel = _jobCts; _jobCts = null; }
            _reprobeCodex = on;
            _batch = null;
            _current = _current with { Enhance = after.Enhance, EnhanceMinLevel = after.EnhanceMinLevel, EnhanceStyle = after.EnhanceStyle };
        }
        cancel?.Cancel();
        Changed?.Invoke();
        WakeEnhancer();
    }

    /// <summary>
    /// The player asked for the pictures the window offered: exactly those - each a character,
    /// a capture and the settings of the last look - may be made. Nothing else is allowed by it.
    /// </summary>
    public void RemakeEnhanced()
    {
        int count;
        lock (_gate)
        {
            count = _held.Count;
            foreach (var h in _held) _remakeAllowed.Add(h.Signature);
            if (count > 0) SaveRemake();
            _batch = null;
        }
        if (count == 0) return;
        _log?.Write($"enhance: {count} held picture(s) may be made again with the current settings");
        WakeEnhancer();
    }

    /// <summary>The picture in flight, if any, is given up; the loop stops at its next wake.</summary>
    private void StopEnhancer()
    {
        CancellationTokenSource? cancel;
        lock (_gate) { cancel = _jobCts; _jobCts = null; }
        cancel?.Cancel();
        WakeEnhancer();
    }
}
