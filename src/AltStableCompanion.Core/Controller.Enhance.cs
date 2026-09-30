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
    /// <summary>The real thing: the CLI on PATH, its own home, its login status.</summary>
    public static EnhanceHooks Real()
    {
        var exe = CodexImageGen.Find();
        var gen = exe is null ? null : new CodexImageGen(exe, CodexImageGen.DefaultHome());
        return new(
            (job, dir, ct) => gen is null
                ? Task.FromResult(new CodexResult(null, "codex is not on PATH", ""))
                : gen.GenerateAsync(job, dir, ct),
            async ct => exe is null ? null : await CodexImageGen.LoginStatusAsync(exe, ct) ?? "installed");
    }
}

public sealed partial class Controller
{
    private readonly SemaphoreSlim _enhanceWake = new(0, 1);
    private CancellationTokenSource? _jobCts;               // the generation in flight, if any
    private Task? _enhanceLoop;
    private string? _codexStatus;                            // null: not found; else how it is signed in
    private bool _codexProbed;
    private string? _enhanceRefused;                         // why nobody is eligible right now, logged once

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
                while (true)
                {
                    var job = PrepareEnhancement();
                    if (job is null) break;
                    CancellationTokenSource cts;
                    lock (_gate)
                    {
                        if (_stopping) { AbandonJob(job, "the app is stopping"); return; }
                        cts = _jobCts = new CancellationTokenSource();
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
                    cts.Dispose();
                    PublishEnhancement(job, result);
                }
            }
            catch (Exception ex)
            {
                _log?.Write($"the enhancer stopped on an error and will try again on the next wake: {ex}");
            }
        }
    }

    private async Task ProbeCodexAsync()
    {
        bool probe;
        lock (_gate) { probe = !_codexProbed && _settings.Enhance; }
        if (!probe) return;
        var status = await _enhance!.CodexFound(CancellationToken.None);
        lock (_gate)
        {
            _codexProbed = true;
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
            lock (_gate)
            {
                if (_stopping || !_settings.Enhance || _current.Shell.Install is null || _current.Shell.FirstStart || _current.Shell.Paused) return Idle(null);
                settings = _settings;
                install = _current.Shell.Install;
                generation = _generation;
                codex = _codexStatus;
            }
            if (codex is null) return Idle("the Codex CLI is not on this PC");
            if (!install.RosterDrawsEnhanced) return Idle("the installed AltStable Roster cannot draw enhanced pictures");

            var folder = new CutoutFolder(install.CutoutAddonDir);
            if (!folder.Exists) return Idle("no portraits yet");
            var snapshot = SavedVariablesReader.Snapshot(install.AccountsDir, m => _log?.Write(m));
            var eligible = Eligibility.Select(snapshot, settings.EnhanceMinLevel, guid => folder.FileBaseOf(guid));
            if (eligible.Refused is { } refused) return Idle(refused);

            var candidates = eligible.Candidates;
            for (var i = 0; i < candidates.Count; i++)
            {
                var c = candidates[i];
                var primary = Path.Combine(folder.CutoutsDir, c.FileBase + ".tga");
                var meta = folder.ReadMeta(c.FileBase);
                if (meta?.Guid != c.Guid || !File.Exists(primary)) continue;
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
                if (history.Has(signature)) continue;

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
                    _current = _current with { Shell = _current.Shell with { Enhancing = $"{c.Character.Name} ({i + 1} of {candidates.Count})" } };
                }
                Changed?.Invoke();
                _log?.Write($"enhance: {c.Character.Name} ({c.FileBase}), {settings.EnhanceStyle}, {settings.EnhanceModel} {settings.EnhanceEffort}");
                return new EnhanceJob(c.Guid, c.FileBase, c.Character.Name, signature, sourceHash, meta, settings.EnhanceStyle,
                    dir, codexJob, generation, install.CutoutAddonDir);
            }
            return Idle(null);
        }
    }

    // Nothing to do, and why - said once in the log when it is a reason.
    private EnhanceJob? Idle(string? because)
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
        }
        if (changed) Changed?.Invoke();
        return null;
    }

    // The job never ran: the record it left says so.
    private void AbandonJob(EnhanceJob job, string why)
    {
        try
        {
            AttemptHistory.Load(new CutoutFolder(job.InstallDir).EnhancedDir, job.Guid).End(job.Signature, Attempt.Cancelled + ": " + why, _clock());
        }
        catch (AttemptHistoryException ex)
        {
            _log?.Write($"enhance: {ex.Message}");
        }
        TryDeleteDir(job.Dir);
    }

    /// <summary>
    /// What came back, decoded and checked OUTSIDE the gates; then, under them, the record and
    /// the files, in that order - the record first, so a crash between the two leaves an
    /// attempt that says "written" with an output hash the next start can look for.
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
            bool stillWanted;
            lock (_gate)
            {
                stillWanted = !_stopping && job.Generation == _generation && _settings.Enhance;
            }
            if (outcome == Attempt.Written)
            {
                if (!stillWanted) outcome = Attempt.Cancelled + ": no longer wanted when it came back";
                else
                {
                    // The portrait it was made from must still be the portrait: a new capture
                    // since means a new signature, and this picture is nobody's.
                    string now;
                    try { now = EnhancementSignature.HashOf(Path.Combine(folder.CutoutsDir, job.Base + ".tga")); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { now = ""; }
                    if (now != job.SourceHash) outcome = Attempt.Failed + ": the portrait changed while the picture was made";
                }
            }
            try
            {
                AttemptHistory.Load(folder.EnhancedDir, job.Guid).End(job.Signature, outcome, _clock(), outputHash);
            }
            catch (AttemptHistoryException ex)
            {
                _log?.Write($"enhance: {ex.Message}");
            }
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
                    outcome = Attempt.Failed + ": could not be written";
                }
            }
            else
            {
                _log?.Write($"enhance: {job.Name}: {outcome}");
            }
            RefreshAfterEnhancement(folder, job, outcome == Attempt.Written);
        }
        TryDeleteDir(job.Dir);
        Changed?.Invoke();
    }

    // Under _passGate. The manifest and the rows again, from the folder as it is now; the
    // notice, when a picture was written.
    private void RefreshAfterEnhancement(CutoutFolder folder, EnhanceJob job, bool written)
    {
        IReadOnlyList<ManifestEntry> entries;
        IReadOnlyDictionary<string, DateTime> times;
        try
        {
            entries = folder.Inventory(m => _log?.Write(m));
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
            if (job.Generation != _generation || _current.Shell.Report is not { } report) return;
            var rows = Collection.Build(entries, report.Characters, times, folder.EnhanceNote);
            var note = written ? new PassNote(_clock(), [], Enhanced: [job.Name]) : null;
            _current = _current with
            {
                Shell = _current.Shell with
                {
                    Report = report with { Portraits = rows },
                    LastWritten = note ?? _current.Shell.LastWritten,
                    UpdateSeen = note is null && _current.Shell.UpdateSeen,
                },
            };
        }
    }

    private void TryDeleteDir(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _log?.Write($"enhance: {dir} could not be deleted: {ex.Message}"); }
    }

    /// <summary>The enhancement settings, from the window. Turning it off cancels the picture in flight.</summary>
    public void SetEnhance(bool on, int minLevel, string style)
    {
        CancellationTokenSource? cancel = null;
        lock (_gate)
        {
            var before = (_settings.Enhance, _settings.EnhanceMinLevel, _settings.EnhanceStyle);
            var after = new Settings { Enhance = on, EnhanceMinLevel = minLevel, EnhanceStyle = style };
            if (before == (after.Enhance, after.EnhanceMinLevel, after.EnhanceStyle)) return;
            if (!on) { cancel = _jobCts; _jobCts = null; }
            _current = _current with { Enhance = after.Enhance, EnhanceMinLevel = after.EnhanceMinLevel, EnhanceStyle = after.EnhanceStyle };
        }
        cancel?.Cancel();
        Save(s => s with { Enhance = on, EnhanceMinLevel = minLevel, EnhanceStyle = style });
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
