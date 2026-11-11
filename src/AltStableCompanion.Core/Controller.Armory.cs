namespace AltStableCompanion.Core;

/// <summary>
/// Portraits from the armory's renders (#63): one worker, one request at a time, a second
/// apart, only when the player said yes. What it knows of each character is in memory; the
/// window after play comes from the roster's own times, so a restart finds it again.
/// </summary>
public sealed partial class Controller
{
    /// <summary>The gap between two requests.</summary>
    public static readonly TimeSpan ArmoryGap = TimeSpan.FromSeconds(1);

    /// <summary>How often the worker looks at who is due when nothing wakes it.</summary>
    public static readonly TimeSpan ArmoryTick = TimeSpan.FromMinutes(1);

    private sealed class ArmoryState
    {
        public DateTimeOffset? LastChecked;
        public bool Checked;          // this run: the sweep at the start, or since "Check the armory now"
        public bool Held;             // skipped for the enhancement hold: due once when it ends
        public string? Note;
    }

    private readonly SemaphoreSlim _armoryWake = new(0, 1);
    private readonly CancellationTokenSource _armoryStop = new();
    private Task? _armoryLoop;
    private ArmoryClient? _armoryClient;
    private readonly Dictionary<string, ArmoryState> _armory = new(StringComparer.Ordinal);   // under _gate
    private Dictionary<string, string> _armoryNames = new(StringComparer.Ordinal);              // under _gate: eligible, by GUID
    private DateTimeOffset _armoryBusyUntil;                                                    // under _gate
    private string? _armoryRefused;                                                             // under _gate: logged once
    private int _armoryGeneration = -1;                                                         // under _gate

    private void WakeArmory()
    {
        try { _armoryWake.Release(); } catch (SemaphoreFullException) { }
    }

    private void StartArmory()
    {
        if (armoryHttp is null) return;
        _armoryClient = new ArmoryClient(armoryHttp, update?.Version ?? AppVersion.Informational);
        _armoryLoop = Task.Run(ArmoryLoopAsync);
        WakeArmory();
    }

    private void StopArmory()
    {
        try { _armoryStop.Cancel(); } catch (ObjectDisposedException) { }
        WakeArmory();
    }

    /// <summary>"Check the armory now": every eligible character once more, at once.</summary>
    public void CheckArmoryNow()
    {
        lock (_gate)
        {
            foreach (var s in _armory.Values) s.Checked = false;
        }
        _log?.Write("armory: a check of every character asked for");
        WakeArmory();
    }

    /// <summary>The player's answer to "portraits from the armory?", or a change of it in Settings.</summary>
    public void SetArmory(bool on)
    {
        lock (_gate)
        {
            if (_settings.Armory == on) return;
            _current = _current with { Armory = on };
        }
        Save(s => s with { Armory = on });
        _log?.Write("armory: turned " + (on ? "on" : "off"));
        if (!on) RefreshArmoryRows();
        WakeArmory();
    }

    /// <summary>HD or SD: another URL, so every character is looked at again.</summary>
    public void SetArmorySd(bool sd)
    {
        lock (_gate)
        {
            if (_settings.ArmorySd == sd) return;
            _current = _current with { ArmorySd = sd };
            foreach (var s in _armory.Values) s.Checked = false;
        }
        Save(s => s with { ArmorySd = sd });
        WakeArmory();
    }

    public void SetArmoryRemakeDays(int days)
    {
        days = Math.Clamp(days, Armory.MinRemakeDays, Armory.MaxRemakeDays);
        lock (_gate)
        {
            if (_settings.ArmoryRemakeDays == days) return;
            _current = _current with { ArmoryRemakeDays = days };
            _batch = null;
        }
        Save(s => s with { ArmoryRemakeDays = days });
        WakeArmory();
        WakeEnhancer();
    }

    /// <summary>Whether a new armory picture is enhanced on its own, and the daily limit on that.</summary>
    public void SetArmoryEnhance(bool auto, bool limit, int max)
    {
        max = Math.Clamp(max, 1, Armory.MaxDailyMax);
        lock (_gate)
        {
            if (_settings.ArmoryEnhanceAuto == auto && _settings.ArmoryDailyLimit == limit && _settings.ArmoryDailyMax == max) return;
            _current = _current with { ArmoryEnhanceAuto = auto, ArmoryDailyLimit = limit, ArmoryDailyMax = max };
            _batch = null;
        }
        Save(s => s with { ArmoryEnhanceAuto = auto, ArmoryDailyLimit = limit, ArmoryDailyMax = max });
        WakeEnhancer();
    }

    private async Task ArmoryLoopAsync()
    {
        var stop = _armoryStop.Token;
        while (!stop.IsCancellationRequested)
        {
            try
            {
                await _armoryWake.WaitAsync(ArmoryTick, stop);
                await ArmoryCycleAsync(stop);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _log?.Write($"armory: stopped on an error and will look again in a minute: {ex}");
            }
        }
    }

    private DateTimeOffset ArmoryNow() => new(_clock());

    // One look at everyone: who is due is asked, one at a time.
    private async Task ArmoryCycleAsync(CancellationToken stop)
    {
        Settings settings;
        WowInstall? install;
        int generation;
        lock (_gate)
        {
            if (_stopping) return;
            settings = _settings;
            install = _current.Shell.Install;
            generation = _generation;
            if (_armoryGeneration != generation)
            {
                // Another game: nothing known of this one yet.
                _armoryGeneration = generation;
                _armory.Clear();
                _armoryNames = new(StringComparer.Ordinal);
            }
            if (settings.Armory != true || install is null || _current.Shell.Paused) return;
        }
        var region = Armory.RegionOf(install);
        if (region != Armory.Region)
        {
            ArmoryRefuse(region is null ? "the game's region is not in Config.wtf" : $"armory renders are not available in your region ({region}) yet");
            return;
        }
        var snapshot = SavedVariablesReader.Snapshot(install.AccountsDir, m => _log?.Write(m));
        var (characters, refused) = ArmoryEligibility.Select(snapshot);
        if (refused is not null)
        {
            ArmoryRefuse(refused + ": not knowing who is hidden is not permission");
            return;
        }
        lock (_gate)
        {
            _armoryRefused = null;
            _armoryNames = characters.ToDictionary(c => c.Guid, c => c.Name, StringComparer.Ordinal);
            foreach (var gone in _armory.Keys.Where(k => !_armoryNames.ContainsKey(k)).ToList()) _armory.Remove(gone);
        }

        var folder = new CutoutFolder(install.CutoutAddonDir);
        IReadOnlyDictionary<string, (string Base, CutoutMeta Meta)>? renders = null;
        var changed = false;
        foreach (var c in characters)
        {
            var now = ArmoryNow();
            ArmoryState state;
            lock (_gate)
            {
                if (_stopping || generation != _generation || _settings.Armory != true || _current.Shell.Paused) break;
                settings = _settings;
                if (now < _armoryBusyUntil) break;
                if (!_armory.TryGetValue(c.Guid, out state!)) _armory[c.Guid] = state = new ArmoryState();
            }
            // The enhancement hold: with enhancement on, a character whose last armory picture is
            // under N days old is not looked at - the render would only wait for the enhancer.
            if (settings.Enhance && HeldUntil(folder, c.Guid, settings.ArmoryRemakeDays) is { } until)
            {
                changed |= Note(state, $"armory updates resume on {until:d MMM}", held: true);
                continue;
            }
            if (!ArmoryPlanner.Due(c, state.LastChecked, state.Checked, state.Held, now)) continue;
            var url = Armory.Url(c.Guid, settings.ArmorySd)!;
            renders ??= folder.ArmoryRenders();
            // Validators only for the file they describe: a render of another URL (HD/SD), or a
            // file that is not whole, is fetched whole.
            var have = renders.TryGetValue(c.Guid, out var r) && r.Meta.Url == url ? r.Meta : null;
            var fetch = await _armoryClient!.FetchAsync(url, have?.ETag, have?.LastModified, stop);
            lock (_gate)
            {
                state.LastChecked = now;
                state.Checked = true;
                state.Held = false;
            }
            switch (fetch.Status)
            {
                case ArmoryStatus.NotModified:
                    changed |= Note(state, null);
                    break;
                case ArmoryStatus.NoRender:
                    changed |= Note(state, "no armory render yet");
                    break;
                case ArmoryStatus.Busy:
                    lock (_gate) _armoryBusyUntil = now + (fetch.RetryAfter ?? ArmoryClient.DefaultBusy);
                    _log?.Write($"armory: {fetch.Problem}; waiting {(fetch.RetryAfter ?? ArmoryClient.DefaultBusy).TotalMinutes:0} minutes");
                    changed |= Note(state, "the armory asked to wait");
                    break;
                case ArmoryStatus.Failed:
                    _log?.Write($"armory: {c.Name}: {fetch.Problem}");
                    changed |= Note(state, "armory: " + fetch.Problem);
                    break;
                case ArmoryStatus.Ok:
                    var note = PublishArmory(install, generation, c, url, fetch);
                    changed |= Note(state, note);
                    renders = null;
                    break;
            }
            await Task.Delay(ArmoryGap, stop);
        }
        if (changed) RefreshArmoryRows();
    }

    // Under nothing. True when the row's words changed.
    private bool Note(ArmoryState state, string? note, bool held = false)
    {
        lock (_gate)
        {
            if (held) state.Held = true;
            if (state.Note == note) return false;
            state.Note = note;
            return true;
        }
    }

    private void ArmoryRefuse(string why)
    {
        lock (_gate)
        {
            if (_armoryRefused == why) return;
            _armoryRefused = why;
        }
        _log?.Write("armory: " + why);
    }

    /// <summary>When the character's armory updates may run again, or null when nothing holds them. A history that cannot be read holds them.</summary>
    private DateTime? HeldUntil(CutoutFolder folder, string guid, int days)
    {
        try
        {
            return Armory.HeldUntil(AttemptHistory.Load(folder.EnhancedDir, guid), days, _clock());
        }
        catch (Exception ex) when (ex is AttemptHistoryException or ArgumentException)
        {
            return DateTime.MaxValue;
        }
    }

    /// <summary>
    /// A render that came back: decoded and cut out here, outside the gates; written under them,
    /// the install and the setting checked again. A render with the same pixels as the file on
    /// disk keeps the file - and the enhanced picture made from it - and takes the new date.
    /// Returns the row's note, null when it went well.
    /// </summary>
    private string? PublishArmory(WowInstall install, int generation, ArmoryCharacter c, string url, ArmoryFetch fetch)
    {
        var now = DateTimeOffset.UtcNow;
        long? skew = fetch.Date is { } date && Math.Abs((date - now).TotalSeconds) > Armory.SkewTolerance
            ? (long)(date - now).TotalSeconds : null;
        if (skew is { } s) _log?.Write($"armory: the CDN's clock is {s} seconds from this PC's; renders are dated by it");
        byte[] tga;
        CutoutMeta meta;
        RgbaImage canvas;
        try
        {
            var png = PngCodec.Read(fetch.Body!);
            var cutout = Armory.ToCutout(png, new CutoutMeta
            {
                Guid = c.Guid, Url = url, ETag = fetch.ETag,
                // A render with no date is dated when it arrived: it is at least that old.
                LastModified = fetch.LastModified ?? (fetch.Date ?? now).ToString("R"),
                Fetched = now.UtcDateTime, Skew = skew,
            });
            using var ms = new MemoryStream();
            TgaCodec.Write(ms, cutout.Canvas);
            tga = ms.ToArray();
            meta = cutout.Meta;
            canvas = cutout.Canvas;
        }
        catch (Exception ex) when (ex is PngFormatException or ThumbnailException or ArgumentException)
        {
            _log?.Write($"armory: {c.Name}: the render could not be read: {ex.Message}");
            return "the armory render could not be read";
        }

        lock (_passGate)
        {
            lock (_gate)
            {
                if (_stopping || generation != _generation || _settings.Armory != true) return null;
            }
            // Hidden since the look, or an account that can no longer be read: not written.
            var fresh = ArmoryEligibility.Select(SavedVariablesReader.Snapshot(install.AccountsDir, m => _log?.Write(m)));
            if (fresh.Refused is not null || fresh.Characters.All(x => x.Guid != c.Guid)) return null;
            var folder = new CutoutFolder(install.CutoutAddonDir);
            try
            {
                OweRestartNotice(install);
                folder.EnsureToc();
                var fileBase = folder.BaseFor(c.Guid, c.Name);
                var hash = EnhancementSignature.HashOf(tga);
                var path = Path.Combine(folder.ArmoryDir, fileBase + ".tga");
                var old = folder.ReadArmoryMeta(fileBase);
                // The same picture, drawn again (the weekly reset, a login that changed nothing
                // seen): the file stays, and with it the enhanced picture made from it. Only
                // its date moves on.
                if (old is { TgaHash: { } oldHash } && old.Guid == c.Guid && File.Exists(path) && EnhancementSignature.HashOf(path) == oldHash
                    && (oldHash == hash || Armory.NearlySame(TgaCodec.Read(path), canvas)))
                {
                    folder.WriteArmoryMeta(fileBase, meta with { TgaHash = oldHash, W = old.W, H = old.H, TexW = old.TexW, TexH = old.TexH, NativePx = old.NativePx });
                    if (oldHash != hash) _log?.Write($"armory: {c.Name}: drawn again, the same picture - the file is kept");
                }
                else
                {
                    folder.WriteArmory(fileBase, tga, meta);
                    _log?.Write($"armory: wrote Armory\\{fileBase}.tga for {c.Name} (rendered {meta.LastModified})");
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TgaFormatException)
            {
                _log?.Write($"armory: {c.Name}: the render could not be written: {ex.Message}");
                return "the armory render could not be written";
            }
            RefreshAfterEnhancement(folder, generation, null);
            lock (_gate) _current = _current with { RestartNotice = RestartNoticeDue(install) };
        }
        Changed?.Invoke();
        WakeEnhancer();
        return null;
    }

    /// <summary>The rows again, with what the armory said of each character: after a look, or a setting.</summary>
    private void RefreshArmoryRows()
    {
        lock (_gate)
        {
            if (_current.Shell.Report is not { Portraits: { } rows } report) return;
            _current = _current with { Shell = _current.Shell with { Report = report with { Portraits = WithArmory(rows) } } };
        }
        Changed?.Invoke();
    }

    // Under _gate. Each row with the armory's note; a render's file row named after its
    // character; and a row for every eligible character the list does not have yet. Built
    // from rows that may already carry this: what was added before is taken out first.
    private IReadOnlyList<PortraitRow> WithArmory(IReadOnlyList<PortraitRow> rows)
    {
        var on = _settings.Armory == true;
        var kept = rows.Where(r => !(r.FileName is null && r.Outcome == CaptureOutcome.None && r.LatestCapture is null)).ToList();
        string? NoteOf(string? guid) => on && guid is not null && _armory.TryGetValue(guid, out var s) ? s.Note : null;
        var result = kept.Select(r => r with
        {
            ArmoryNote = NoteOf(r.Guid),
            Name = r.Source == PortraitSource.File && r.Guid is not null && _armoryNames.TryGetValue(r.Guid, out var name) ? name : r.Name,
        }).ToList();
        if (on)
        {
            var listed = result.Where(r => r.Guid is not null).Select(r => r.Guid!).ToHashSet(StringComparer.Ordinal);
            foreach (var (guid, name) in _armoryNames.OrderBy(kv => kv.Value, StringComparer.OrdinalIgnoreCase))
            {
                if (listed.Contains(guid)) continue;
                result.Add(new PortraitRow(name, guid, null, PortraitSource.None, null, null, CaptureOutcome.None, null,
                    ArmoryNote: NoteOf(guid) ?? "waiting for the armory"));
            }
        }
        return result;
    }
}
