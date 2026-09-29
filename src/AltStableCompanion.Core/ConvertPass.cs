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
/// <item>a rejected pair stays on disk and is not decoded again until either file changes.</item>
/// </list>
/// Keep one instance for the app's lifetime: the rejected-pair memory lives on it.
/// </summary>
public sealed class ConvertPass(WowInstall install, ConvertOptions options, Action<string>? log = null)
{
    private readonly Dictionary<string, string> _rejected = new(StringComparer.OrdinalIgnoreCase);

    public PassReport Run(CancellationToken ct = default)
    {
        var warnings = new List<string>();
        var stores = SavedVariablesReader.ReadAll(install.AccountsDir, m => { warnings.Add(m); log?.Invoke(m); });
        foreach (var refused in stores.Where(s => s.Refused is not null))
        {
            warnings.Add($"{refused.Path}: {refused.Refused}");
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
        foreach (var cap in newest)
        {
            var existing = folder.FileBaseOf(cap.Guid);
            if (existing is not null && folder.ReadMeta(existing)?.Epoch == EpochOf(cap))
            {
                statuses.Add(new CharacterStatus(cap.Guid, cap.Name, cap.First, CharacterState.Portrait, null));
            }
            else
            {
                todo.Add(cap);
            }
        }

        foreach (var a in CapturePairing.Assign(todo, times))
        {
            ct.ThrowIfCancellationRequested();
            var cap = a.Capture;
            switch (a.Problem)
            {
                case MatchProblem.Collided:
                    statuses.Add(Status(cap, CharacterState.Collided,
                        $"both shots landed in the same second ({cap.First:HH:mm:ss}) - capture again"));
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
            if (_rejected.TryGetValue(fingerprint, out var why))
            {
                statuses.Add(Status(cap, CharacterState.Rejected, why));
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
                _rejected[fingerprint] = ex.Message;
                statuses.Add(Status(cap, CharacterState.Rejected, ex.Message));
                continue;
            }
            catch (Exception ex) when (ex is IOException or TgaFormatException)
            {
                statuses.Add(Status(cap, CharacterState.Missing, "a screenshot is still being written - next pass"));
                continue;
            }

            if (!folderCreated && folder.EnsureToc()) folderCreated = true;
            var fileBase = folder.FileBaseOf(cap.Guid) ?? Slugger.FileBase(cap.Name, cap.Guid, folder.OwnerOf);
            folder.WriteCutout(fileBase, cutout.Canvas, cutout.Meta);
            written.Add(cap.Name);
            log?.Invoke($"wrote {fileBase}.tga for {cap.Name}");
            statuses.Add(Status(cap, CharacterState.Portrait, cutout.NearlySquare
                ? "nearly square - something other than the character may have been on screen; re-capture"
                : null));

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
                    catch (IOException ex)
                    {
                        warnings.Add($"could not delete {Path.GetFileName(spent)}: {ex.Message}");
                    }
                }
            }
        }

        if (folder.Exists)
        {
            if (!folderCreated) folder.EnsureToc();
            folder.WriteManifest(DateTime.Now);
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

    private static string Fingerprint(string black, string white)
    {
        static string Of(string p)
        {
            var f = new FileInfo(p);
            return f.Exists ? $"{p}|{f.Length}|{f.LastWriteTimeUtc.Ticks}" : p;
        }
        return Of(black) + "||" + Of(white);
    }

    // A screenshot can still be being written when the watcher fires.
    private static RgbaImage ReadWithRetry(string path)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return TgaCodec.Read(path);
            }
            catch (Exception ex) when (attempt < 5 && ex is IOException or TgaFormatException)
            {
                Thread.Sleep(500);
            }
        }
    }
}
