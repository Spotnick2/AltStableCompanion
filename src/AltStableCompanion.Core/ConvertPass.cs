namespace AltStableCompanion.Core;

public sealed record ConvertOptions(bool KeepScreenshots = false);

public enum CharacterState
{
    /// <summary>A portrait of the newest capture is on disk.</summary>
    Portrait,
    /// <summary>The screenshots are not on disk (yet, or any more).</summary>
    Missing,
    /// <summary>The files on disk do not clearly belong to this capture; nothing was touched.</summary>
    Ambiguous,
    /// <summary>Both shots landed in one second; the client kept one file. Re-capture.</summary>
    Collided,
    /// <summary>The pair was looked at and is not a portrait (identical shots, the whole window...).</summary>
    Rejected,
    /// <summary>Something went wrong converting it this pass (a file in use, a folder that refuses writes).</summary>
    Failed,
}

public sealed record CharacterStatus(string Guid, string Name, DateTime LastCaptured, CharacterState State, string? Note);

public sealed record PassReport(
    IReadOnlyList<CharacterStatus> Characters,
    IReadOnlyList<string> Written,
    long BytesFreed,
    (DateTime NewestShot, DateTime? NewestRecord)? Stale,
    bool FolderCreated,
    IReadOnlyList<string> Warnings);

/// <summary>
/// One full, idempotent pass: read every store, pair the newest capture of each character with
/// its screenshots, convert what is new, rebuild the manifest. Ported from make-cutout.py's
/// run_all and Update-Cutouts.ps1, with the contract's stricter rules:
/// <list type="bullet">
/// <item>"already converted" is the sidecar's guid + epoch, checked BEFORE looking for
///   screenshots - so a capture whose screenshots were consumed reads as done, not missing;</item>
/// <item>only the two files a capture consumed are deleted, after its cutout is written - no
///   superseded-file clean-up, which matched old stamps to whatever file was nearest;</item>
/// <item>a pair that was looked at and cannot be used stays on disk and is not decoded again
///   until either file changes;</item>
/// <item>one capture that fails does not take the others, or the manifest, with it.</item>
/// </list>
/// Keep one instance for the app's lifetime: the memory of unusable pairs lives on it.
/// </summary>
public sealed class ConvertPass(WowInstall install, ConvertOptions options, Action<string>? log = null)
{
    private const string SquareNote =
        "nearly square - something other than the character may have been on screen; re-capture";

    private readonly Dictionary<string, (CharacterState State, string Note)> _unusable = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _logged = [];

    /// <summary>
    /// Cancelling stops the pass BETWEEN captures and lets it finish: the manifest is still
    /// rebuilt and the report still returned, so a cutout written before the stop is listed
    /// and reported. It does not throw.
    /// </summary>
    public PassReport Run(CancellationToken ct = default)
    {
        var warnings = new List<string>();
        // Warnings are in every report; the log gets each one once, not once a minute.
        void Warn(string message)
        {
            warnings.Add(message);
            if (_logged.Add(message)) log?.Invoke(message);
        }

        var stores = SavedVariablesReader.ReadAll(install.AccountsDir, Warn);
        foreach (var refused in stores.Where(s => s.Refused is not null))
        {
            Warn($"{refused.Path}: {refused.Refused}");
        }

        var newest = CapturePairing.NewestPerGuid(CapturePairing.Pair(stores));
        var times = CapturePairing.ShotTimes(install.Screenshots);
        var stale = CapturePairing.StoreIsStale(stores.Where(s => s.Refused is null), times);
        var folder = new CutoutFolder(install.CutoutAddonDir);

        var statuses = new List<CharacterStatus>();
        var written = new List<string>();
        var freed = 0L;
        var folderCreated = false;

        // Done already: this character's cutout is on disk and was made from THIS capture.
        var todo = new List<Capture>();
        // A cutout make-cutout.py wrote carries the guid and no epoch: whose it is, not which
        // capture it came from.
        var undated = new HashSet<string>();
        foreach (var cap in newest)
        {
            var existing = folder.FileBaseOf(cap.Guid);
            var meta = existing is null ? null : folder.ReadMeta(existing);
            if (meta?.Epoch == EpochOf(cap))
            {
                statuses.Add(Status(cap, CharacterState.Portrait, meta.NearlySquare ? SquareNote : null));
                continue;
            }
            if (meta is { Epoch: null }) undated.Add(cap.Guid);
            todo.Add(cap);
        }

        foreach (var a in CapturePairing.Assign(todo, times))
        {
            if (ct.IsCancellationRequested) break;
            var cap = a.Capture;
            try
            {
                switch (a.Problem)
                {
                    case MatchProblem.Collided:
                        statuses.Add(Status(cap, CharacterState.Collided,
                            $"both shots landed in the same second ({cap.First:HH:mm:ss}) - capture again"));
                        continue;
                    case MatchProblem.Missing when undated.Contains(cap.Guid):
                        // The portrait is there and drawn; the converter that made it consumed
                        // the screenshots and did not say which capture they were.
                        statuses.Add(Status(cap, CharacterState.Portrait,
                            "made by an earlier converter, which did not record its capture"));
                        continue;
                    case MatchProblem.Missing:
                        statuses.Add(Status(cap, CharacterState.Missing, "no screenshots for this capture on disk"));
                        continue;
                    case MatchProblem.Ambiguous:
                        statuses.Add(Status(cap, CharacterState.Ambiguous,
                            "the screenshots on disk do not clearly belong to this capture - left alone"));
                        continue;
                }

                var black = a.Black!;
                var white = a.White!;
                var fingerprint = Fingerprint(black, white);
                if (_unusable.TryGetValue(fingerprint, out var known))
                {
                    statuses.Add(Status(cap, known.State, known.Note));
                    continue;
                }

                Cutout cutout;
                try
                {
                    var blackImg = ReadWithRetry(black);
                    var whiteImg = ReadWithRetry(white);
                    cutout = CutoutConverter.Convert(blackImg, whiteImg, cap.Guid, EpochOf(cap));
                }
                catch (NotAPairException ex)
                {
                    statuses.Add(Status(cap, Remember(fingerprint, CharacterState.Rejected, ex.Message)));
                    continue;
                }
                catch (TgaFormatException ex) when (ex.Truncated)
                {
                    // Still being written, or cut short for good. Either way there is nothing
                    // to read again until the file changes - and a file being written does.
                    statuses.Add(Status(cap, Remember(fingerprint, CharacterState.Missing,
                        "a screenshot ends early - read again when it changes")));
                    continue;
                }
                catch (TgaFormatException ex)
                {
                    statuses.Add(Status(cap, Remember(fingerprint, CharacterState.Rejected,
                        $"a screenshot cannot be read: {ex.Message}")));
                    continue;
                }
                catch (IOException)
                {
                    // In use by something else. Its size and time will not change when that
                    // ends, so this one is NOT remembered.
                    statuses.Add(Status(cap, CharacterState.Missing, "a screenshot is in use - next pass"));
                    continue;
                }

                if (folder.EnsureToc()) folderCreated = true;
                var fileBase = folder.FileBaseOf(cap.Guid) ?? Slugger.FileBase(cap.Name, cap.Guid, folder.OwnerOf);
                folder.WriteCutout(fileBase, cutout.Canvas, cutout.Meta);
                written.Add(cap.Name);
                log?.Invoke($"wrote {fileBase}.tga for {cap.Name}");
                statuses.Add(Status(cap, CharacterState.Portrait, cutout.NearlySquare ? SquareNote : null));

                // Only now, and only these two: a failed convert leaves its source to retry, and a
                // file this capture did not consume is not ours to delete.
                if (!options.KeepScreenshots)
                {
                    foreach (var spent in new[] { black, white })
                    {
                        try
                        {
                            var size = new FileInfo(spent).Length;
                            File.Delete(spent);
                            freed += size;
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                        {
                            Warn($"could not delete {Path.GetFileName(spent)}: {ex.Message}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                // Whatever it was, it was this capture's. The others still get their turn, and
                // the manifest is still written.
                Warn($"{cap.Name}: {ex.Message}");
                statuses.Add(Status(cap, CharacterState.Failed, ex.Message));
            }
        }

        try
        {
            if (folder.Exists)
            {
                if (folder.EnsureToc()) folderCreated = true;
                folder.WriteManifest(DateTime.Now, Warn);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Warn($"could not write the manifest: {ex.Message}");
        }

        return new PassReport(
            [.. statuses.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)],
            written, freed, stale, folderCreated, warnings);
    }

    // The capture's identity in a sidecar: the shot-1 epoch, or its local stamp for a record
    // made before epochs existed.
    private static long EpochOf(Capture cap) => (long)cap.OrderKey;

    private static CharacterStatus Status(Capture cap, CharacterState state, string? note) =>
        new(cap.Guid, cap.Name, cap.First, state, note);

    private static CharacterStatus Status(Capture cap, (CharacterState State, string Note) what) =>
        Status(cap, what.State, what.Note);

    private (CharacterState State, string Note) Remember(string fingerprint, CharacterState state, string note) =>
        _unusable[fingerprint] = (state, note);

    private static string Fingerprint(string black, string white)
    {
        static string Of(string p)
        {
            var f = new FileInfo(p);
            return f.Exists ? $"{p}|{f.Length}|{f.LastWriteTimeUtc.Ticks}" : p;
        }
        return Of(black) + "||" + Of(white);
    }

    // A screenshot can still be being written when the watcher fires: a file that ends early or
    // is in use is read again. One this codec does not understand is not - it will not change.
    private static RgbaImage ReadWithRetry(string path)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return TgaCodec.Read(path);
            }
            catch (Exception ex) when (attempt < 5 && ex is IOException or TgaFormatException { Truncated: true })
            {
                Thread.Sleep(500);
            }
        }
    }
}
