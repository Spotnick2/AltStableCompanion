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
        public DateTime? HeldUntil;   // when the hold ends, while it holds this character
        public bool Forced;           // "Check now" on its row: asked once, past the hold
        public string? Note;
        // What the player's own API client said (M3): levels only go up, a login only later.
        public bool ApiChecked;       // this run
        public DateTimeOffset? ApiLast;
        public int ApiLevel;
        public DateTimeOffset? ApiLogin;
    }

    /// <summary>How often the API is asked about a character, whatever the CDN's hours after play.</summary>
    public static readonly TimeSpan ApiEvery = TimeSpan.FromHours(6);

    private BlizzardApi? _blizzard;
    private BlizzardClient? _blizzardClient;                                                    // under _gate
    private bool _blizzardUnreadable;                                                           // under _gate
    private bool _blizzardRefused;                                                              // under _gate: this run, until another client or a Test that passes
    private int _blizzardRevision;                                                              // under _gate: a client removed or replaced drops answers in flight
    private bool _blizzardNotDeleted;                                                           // under _gate: removed, but its file is still there

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
        _blizzard = new BlizzardApi(armoryHttp, update?.Version ?? AppVersion.Informational, ArmoryNow);
        bool removed;
        lock (_gate) removed = _settings.BlizzardRemoved;
        BlizzardClient? client = null;
        var unreadable = false;
        var notDeleted = false;
        // Removed before, its file not deleted: never used again. Deleted now, if it can be.
        if (removed)
        {
            notDeleted = !DeleteBlizzardClient();
            if (!notDeleted) Save(s => s with { BlizzardRemoved = false }, quiet: true);
        }
        else
        {
            client = BlizzardClientStore.Load(_dataDir, out unreadable);
        }
        lock (_gate)
        {
            _blizzardClient = client;
            _blizzardUnreadable = unreadable;
            _blizzardNotDeleted = notDeleted;
            PublishBlizzardState();
        }
        if (unreadable) _log?.Write("armory: the saved Blizzard API client cannot be read by this Windows user; enter it again");
        _armoryLoop = Task.Run(ArmoryLoopAsync);
        WakeArmory();
    }

    private void StopArmory()
    {
        try { _armoryStop.Cancel(); } catch (ObjectDisposedException) { }
        WakeArmory();
    }

    /// <summary>
    /// "Check now" on a held row: the armory is asked about this character at once, past the
    /// enhancement hold. The click is the player's yes: the picture it leaves as the portrait is
    /// enhanced at once, past the days and the daily limit (<see cref="AskedFor"/>).
    /// </summary>
    public void CheckArmoryNow(string guid)
    {
        lock (_gate)
        {
            if (!_armory.TryGetValue(guid, out var s)) return;
            s.Forced = true;
            s.Checked = false;
            s.HeldUntil = null;
        }
        _log?.Write("armory: a check of one character asked for, past its hold");
        RefreshArmoryRows();
        WakeArmory();
    }

    /// <summary>"Check the armory now": every eligible character once more, at once.</summary>
    public void CheckArmoryNow()
    {
        lock (_gate)
        {
            foreach (var s in _armory.Values) { s.Checked = false; s.ApiChecked = false; }
        }
        _log?.Write("armory: a check of every character asked for");
        WakeArmory();
    }

    /// <summary>The player's answer to "portraits from the armory?", or a change of it in Settings.</summary>
    public void SetArmory(bool on)
    {
        WowInstall? install;
        int generation;
        lock (_gate)
        {
            if (_settings.Armory == on) return;
            _current = _current with { Armory = on };
            if (_pass is not null) _pass.Options = _pass.Options with { Armory = on };
            install = _stopping ? null : _current.Shell.Install;
            generation = _generation;
        }
        Save(s => s with { Armory = on });
        _log?.Write("armory: turned " + (on ? "on" : "off"));
        if (!on) RefreshArmoryRows();
        WakeArmory();
        // The manifest's armory = true follows the answer: on, the addon stops asking for a
        // capture after a gear change for a character the armory draws; off, it asks again.
        if (install is not null) _ = Task.Run(() => RewriteManifest(install, generation));
    }

    private void RewriteManifest(WowInstall install, int generation)
    {
        try
        {
            var folder = new CutoutFolder(install.CutoutAddonDir);
            if (!folder.Exists) return;
            lock (_passGate)
            {
                lock (_gate) if (_stopping || generation != _generation) return;
                RefreshAfterEnhancement(folder, generation, null);
            }
        }
        catch (Exception ex)
        {
            _log?.Write($"armory: the manifest could not be written again: {ex}");
        }
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
        var (everyone, refused) = ArmoryRoster(install);
        if (refused is not null)
        {
            ArmoryRefuse(refused + ": not knowing who is hidden is not permission");
            return;
        }
        lock (_gate)
        {
            _armoryRefused = null;
            var known = everyone.Select(c => c.Guid).ToHashSet(StringComparer.Ordinal);
            foreach (var gone in _armory.Keys.Where(k => !known.Contains(k)).ToList()) _armory.Remove(gone);
        }
        // The player's own API client first: a level or a login it knows decides who the CDN
        // is asked about, and when.
        if (!await ApiSweepAsync(install, generation, everyone, stop)) return;
        var characters = Drawn(Merged(everyone));
        lock (_gate) _armoryNames = characters.ToDictionary(c => c.Guid, c => c.Name, StringComparer.Ordinal);

        var folder = new CutoutFolder(install.CutoutAddonDir);
        IReadOnlyDictionary<string, (string Base, CutoutMeta Meta)>? renders = null;
        var changed = false;
        foreach (var c in characters)
        {
            var now = ArmoryNow();
            ArmoryState state;
            bool forced;
            lock (_gate)
            {
                if (_stopping || generation != _generation || _settings.Armory != true || _current.Shell.Paused) break;
                settings = _settings;
                if (now < _armoryBusyUntil) break;
                if (!_armory.TryGetValue(c.Guid, out state!)) _armory[c.Guid] = state = new ArmoryState();
                forced = state.Forced;
            }
            // The enhancement hold: with enhancement on, a character whose last armory picture is
            // under N days old is not looked at - the render would only wait for the enhancer.
            // Unless the player pressed "Check now" on its row.
            if (settings.Enhance && !forced && HeldUntil(folder, c.Guid, settings.ArmoryRemakeDays) is { } until)
            {
                changed |= Hold(state, until);
                continue;
            }
            if (!ArmoryPlanner.Due(c, state.LastChecked, state.Checked, state.Held, now)) continue;
            // Who may be asked about, read again before each request: a character hidden while
            // the one before was asked, or an account that can no longer be read, is not sent.
            var (fresh, refusedNow) = ArmoryRoster(install);
            if (refusedNow is not null)
            {
                ArmoryRefuse(refusedNow + ": not knowing who is hidden is not permission");
                break;
            }
            if (Drawn(Merged(fresh)).All(x => x.Guid != c.Guid)) continue;
            var url = Armory.Url(c.Guid, settings.ArmorySd)!;
            renders ??= folder.ArmoryRenders();
            // Validators only for the file they describe: a render of another URL (HD/SD), or a
            // file that is not whole, is fetched whole.
            var have = renders.TryGetValue(c.Guid, out var r) && r.Meta.Url == url ? r.Meta : null;
            var fetch = await _armoryClient!.FetchAsync(url, have?.ETag, have?.LastModified, stop);
            bool stale;
            lock (_gate)
            {
                // HD or SD changed while this was asked: the answer is of the other picture, and
                // the character stays due for the one now chosen.
                stale = Armory.Url(c.Guid, _settings.ArmorySd) != url;
                // A busy answer is no answer: the character stays due once everyone may ask again.
                if (!stale && fetch.Status != ArmoryStatus.Busy)
                {
                    state.LastChecked = now;
                    state.Checked = true;
                    state.Held = false;
                    state.HeldUntil = null;
                    state.Forced = false;
                }
            }
            switch (stale && fetch.Status != ArmoryStatus.Busy ? (ArmoryStatus?)null : fetch.Status)
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
            // Asked for by the player: the render now on disk - new, or the one a 304 kept - may
            // be enhanced at once, while it is the portrait. Tied to its bytes, so a later render
            // is not.
            if (forced && !stale && fetch.Status is ArmoryStatus.Ok or ArmoryStatus.NotModified)
            {
                renders ??= folder.ArmoryRenders();
                if (renders.TryGetValue(c.Guid, out var kept) && kept.Meta.TgaHash is { } hash)
                {
                    lock (_gate) _armoryAsked[c.Guid] = hash;
                    WakeEnhancer();
                }
            }
            await Task.Delay(ArmoryGap, stop);
        }
        if (changed) RefreshArmoryRows();
    }

    // Every character not hidden, at any level - the API is asked about them all - or nobody and why.
    private (IReadOnlyList<ArmoryCharacter> Characters, string? Refused) ArmoryRoster(WowInstall install) =>
        ArmoryEligibility.Select(SavedVariablesReader.Snapshot(install.AccountsDir, m => _log?.Write(m)), minLevel: 0);

    // The armory draws from level 10.
    private static IReadOnlyList<ArmoryCharacter> Drawn(IReadOnlyList<ArmoryCharacter> characters) =>
        [.. characters.Where(c => c.Level >= Armory.MinLevel)];

    // What the API said, added in: the higher level (levels only go up, so an old answer never
    // lowers one) and the later play (a login on another PC opens the hours after play too).
    private IReadOnlyList<ArmoryCharacter> Merged(IReadOnlyList<ArmoryCharacter> characters)
    {
        lock (_gate)
        {
            return [.. characters.Select(c => _armory.TryGetValue(c.Guid, out var s)
                ? c with
                {
                    Level = Math.Max(c.Level, s.ApiLevel),
                    Activity = s.ApiLogin is { } login && (c.Activity is not { } played || login > played) ? login : c.Activity,
                }
                : c)];
        }
    }

    // Under _gate.
    private void PublishBlizzardState()
    {
        var state = _blizzardClient is null
            ? (_blizzardNotDeleted ? BlizzardClientState.NotDeleted : _blizzardUnreadable ? BlizzardClientState.Unreadable : BlizzardClientState.None)
            : _blizzardRefused ? BlizzardClientState.Refused : BlizzardClientState.Saved;
        _current = _current with { BlizzardClient = state };
    }

    /// <summary>
    /// The player's own client asked about each character not hidden, at any level: at the start,
    /// then every 6 hours each. Not held by the enhancement hold - it spends nothing. False when
    /// the cycle stops here (the API asked everyone to wait, or the look is over).
    /// </summary>
    private async Task<bool> ApiSweepAsync(WowInstall install, int generation, IReadOnlyList<ArmoryCharacter> everyone, CancellationToken stop)
    {
        foreach (var c in everyone)
        {
            var now = ArmoryNow();
            BlizzardClient client;
            int revision;
            ArmoryState state;
            lock (_gate)
            {
                if (_stopping || generation != _generation || _settings.Armory != true || _current.Shell.Paused) return false;
                if (now < _armoryBusyUntil) return false;
                if (_blizzardClient is not { } held || _blizzardRefused) return true;
                client = held;
                revision = _blizzardRevision;
                if (!_armory.TryGetValue(c.Guid, out state!)) _armory[c.Guid] = state = new ArmoryState();
                if (state.ApiChecked && state.ApiLast is { } last && now - last < ApiEvery) continue;
            }
            // A request names the character: hidden since the sweep began, it is not sent.
            var (fresh, refusedNow) = ArmoryRoster(install);
            if (refusedNow is not null)
            {
                ArmoryRefuse(refusedNow + ": not knowing who is hidden is not permission");
                return false;
            }
            if (fresh.All(x => x.Guid != c.Guid)) continue;
            // And again once a token is in hand, right before the request: a token can take 30 seconds.
            // The client is the one this began with, and the API may be used: before each token too.
            bool Current()
            {
                lock (_gate)
                {
                    return !_stopping && generation == _generation && _settings.Armory == true && !_current.Shell.Paused
                        && revision == _blizzardRevision && !_blizzardRefused && ArmoryNow() >= _armoryBusyUntil;
                }
            }
            bool MayName()
            {
                var (roster, refused) = ArmoryRoster(install);
                return refused is null && roster.Any(x => x.Guid == c.Guid);
            }
            var answer = await _blizzard!.ProfileAsync(client, c.Name, Armory.PlayerNumber(c.Guid)!.Value, Current, MayName, stop);
            bool kept;
            lock (_gate)
            {
                // Removed or replaced while this was asked: not this client's answer to keep.
                kept = revision == _blizzardRevision;
                if (kept)
                {
                    switch (answer.Status)
                    {
                        case BlizzardStatus.Ok:
                            state.ApiLevel = Math.Max(state.ApiLevel, answer.Profile!.Level);
                            if (answer.Profile.LastLogin is { } login && (state.ApiLogin is not { } had || login > had)) state.ApiLogin = login;
                            state.ApiChecked = true;
                            state.ApiLast = now;
                            break;
                        case BlizzardStatus.NotFound:
                        case BlizzardStatus.Failed:
                            state.ApiChecked = true;
                            state.ApiLast = now;
                            break;
                        case BlizzardStatus.Refused:
                            _blizzardRefused = true;
                            PublishBlizzardState();
                            break;
                    }
                }
                // A busy answer holds everyone, whoever's client it was.
                if (answer.Status == BlizzardStatus.Busy) _armoryBusyUntil = now + (answer.RetryAfter ?? ArmoryClient.DefaultBusy);
            }
            if (answer.Status == BlizzardStatus.Busy)
            {
                _log?.Write($"armory: {answer.Problem}; waiting {(answer.RetryAfter ?? ArmoryClient.DefaultBusy).TotalMinutes:0} minutes");
                return false;
            }
            if (kept && answer.Status == BlizzardStatus.Refused)
            {
                _log?.Write($"armory: {answer.Problem}; the armory carries on without the API until the client is tested again");
                Changed?.Invoke();
                await Task.Delay(ArmoryGap, stop);
                return true;
            }
            if (kept && answer.Status == BlizzardStatus.Failed) _log?.Write($"armory: {c.Name}: {answer.Problem}");
            await Task.Delay(ArmoryGap, stop);
        }
        return true;
    }

    /// <summary>
    /// Saves the player's own API client (DPAPI, this Windows user) and uses it from the next
    /// look. Null when it went well, else why not. The id and secret are never shown again.
    /// </summary>
    public string? SetBlizzardClient(string id, string secret)
    {
        id = id.Trim();
        secret = secret.Trim();
        if (id.Length == 0 || secret.Length == 0) return "Enter both the client ID and the client secret.";
        var client = new BlizzardClient(id, secret);
        try
        {
            BlizzardClientStore.Save(_dataDir, client);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException or PlatformNotSupportedException)
        {
            _log?.Write($"armory: the Blizzard API client could not be saved: {ex.GetType().Name}");
            return "The client could not be saved.";
        }
        bool removed;
        lock (_gate)
        {
            removed = _settings.BlizzardRemoved;
            _blizzardClient = client;
            _blizzardUnreadable = false;
            _blizzardNotDeleted = false;
            _blizzardRefused = false;
            // The revision before the token: a request of the old client's sees it is not current.
            _blizzardRevision++;
            foreach (var s in _armory.Values) s.ApiChecked = false;
            PublishBlizzardState();
        }
        _blizzard?.Forget();
        if (removed) Save(s => s with { BlizzardRemoved = false }, quiet: true);
        _log?.Write("armory: a Blizzard API client was saved");
        Changed?.Invoke();
        WakeArmory();
        return null;
    }

    /// <summary>
    /// "Remove": the file, the token and any answer in flight. What the API said is kept for this
    /// run. Null when the file is gone, else why not: the client is not used either way, and the
    /// settings say so, so a file that would not be deleted is not taken back at the next start.
    /// </summary>
    public string? RemoveBlizzardClient()
    {
        var deleted = DeleteBlizzardClient();
        bool removed;
        lock (_gate) removed = _settings.BlizzardRemoved;
        if (removed == deleted) Save(s => s with { BlizzardRemoved = !deleted }, quiet: true);
        lock (_gate)
        {
            _blizzardClient = null;
            _blizzardUnreadable = false;
            _blizzardNotDeleted = !deleted;
            _blizzardRefused = false;
            _blizzardRevision++;
            PublishBlizzardState();
        }
        _blizzard?.Forget();
        _log?.Write(deleted ? "armory: the Blizzard API client was removed" : "armory: the Blizzard API client is no longer used; its file is still there");
        Changed?.Invoke();
        return deleted ? null : PassText.BlizzardState(BlizzardClientState.NotDeleted);
    }

    private bool DeleteBlizzardClient()
    {
        try
        {
            BlizzardClientStore.Remove(_dataDir);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log?.Write($"armory: the Blizzard API client file could not be deleted: {ex.GetType().Name}");
            return false;
        }
    }

    /// <summary>"Test": one token request with the saved client. What to say under the button.</summary>
    public async Task<string> TestBlizzardClientAsync()
    {
        BlizzardClient? client;
        int revision;
        lock (_gate) { client = _blizzardClient; revision = _blizzardRevision; }
        if (client is null || _blizzard is null) return "No client is saved.";
        var answer = await _blizzard.TestAsync(client, CancellationToken.None);
        lock (_gate)
        {
            // A busy answer holds everyone, as in the sweep, whoever's client it was.
            if (answer.Status == BlizzardStatus.Busy) _armoryBusyUntil = ArmoryNow() + (answer.RetryAfter ?? ArmoryClient.DefaultBusy);
            if (revision != _blizzardRevision) return "The client changed meanwhile: test it again.";
            if (answer.Status == BlizzardStatus.Ok)
            {
                _blizzardRefused = false;
                foreach (var s in _armory.Values) s.ApiChecked = false;
            }
            else if (answer.Status == BlizzardStatus.Refused)
            {
                _blizzardRefused = true;
            }
            PublishBlizzardState();
        }
        _log?.Write("armory: the Blizzard API client was tested: " + (answer.Status == BlizzardStatus.Ok ? "accepted" : answer.Problem));
        Changed?.Invoke();
        if (answer.Status == BlizzardStatus.Ok) WakeArmory();
        return answer.Status switch
        {
            BlizzardStatus.Ok => "Battle.net accepted the client.",
            BlizzardStatus.Refused => "Battle.net refused this client ID or secret.",
            _ => "Battle.net could not be asked: " + answer.Problem,
        };
    }

    // Under nothing. True when the row's words changed.
    private bool Note(ArmoryState state, string? note)
    {
        lock (_gate)
        {
            if (state.Note == note) return false;
            state.Note = note;
            return true;
        }
    }

    // The enhancement hold, until when: the row says it, with the time, and offers "Check now".
    private bool Hold(ArmoryState state, DateTime until)
    {
        lock (_gate)
        {
            state.Held = true;
            if (state.HeldUntil == until && state.Note is null) return false;
            state.HeldUntil = until;
            state.Note = null;
            return true;
        }
    }

    /// <summary>
    /// The player asked for this character's armory picture ("Check now"): its render with these
    /// bytes is enhanced at once while it is the portrait, past the days hold, the daily limit and
    /// "enhance on their own". Under _gate. Not taken back once its picture is made: that picture
    /// is in the history, which does not launch it again, and another render has other bytes.
    /// </summary>
    private readonly Dictionary<string, string> _armoryAsked = new(StringComparer.Ordinal);

    private bool AskedFor(string guid, Primary primary)
    {
        lock (_gate) return primary.Armory && _armoryAsked.TryGetValue(guid, out var hash) && hash == primary.Meta.TgaHash;
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
            var fresh = ArmoryRoster(install);
            if (fresh.Refused is not null || Drawn(Merged(fresh.Characters)).All(x => x.Guid != c.Guid)) return null;
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
        DateTime? HeldOf(string? guid) => on && guid is not null && _armory.TryGetValue(guid, out var s) && s.Held && !s.Forced ? s.HeldUntil : null;
        var result = kept.Select(r => r with
        {
            ArmoryNote = NoteOf(r.Guid),
            ArmoryHeldUntil = HeldOf(r.Guid),
            Name = r.Source == PortraitSource.File && r.Guid is not null && _armoryNames.TryGetValue(r.Guid, out var name) ? name : r.Name,
        }).ToList();
        if (on)
        {
            var listed = result.Where(r => r.Guid is not null).Select(r => r.Guid!).ToHashSet(StringComparer.Ordinal);
            foreach (var (guid, name) in _armoryNames.OrderBy(kv => kv.Value, StringComparer.OrdinalIgnoreCase))
            {
                if (listed.Contains(guid)) continue;
                result.Add(new PortraitRow(name, guid, null, PortraitSource.None, null, null, CaptureOutcome.None, null,
                    ArmoryNote: NoteOf(guid) ?? (HeldOf(guid) is null ? "waiting for the armory" : null), ArmoryHeldUntil: HeldOf(guid)));
            }
        }
        return result;
    }
}
