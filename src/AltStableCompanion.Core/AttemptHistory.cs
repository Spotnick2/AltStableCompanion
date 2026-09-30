using System.Text.Json;
using System.Text.Json.Serialization;

namespace AltStableCompanion.Core;

/// <summary>One launch of Codex for one signature, and what came of it.</summary>
public sealed record Attempt
{
    public const string Unknown = "unknown";
    public const string Written = "written";
    public const string Refused = "refused";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";

    public string Signature { get; init; } = "";
    public string? Base { get; init; }
    public DateTime Started { get; init; }
    public DateTime? Ended { get; init; }
    /// <summary>unknown (launched, the app did not live to record the end), written, refused:&lt;check&gt;, failed:&lt;reason&gt;, cancelled.</summary>
    public string Outcome { get; init; } = Unknown;
    public string? OutputHash { get; init; }
    public string? Style { get; init; }
    public string? Model { get; init; }
    public string? Effort { get; init; }
    public int Prompt { get; init; }
    public long? Epoch { get; init; }
    public string? SourceHash { get; init; }
}

/// <summary>
/// Every attempt ever made for one character, in <c>Cutouts\Enhanced\&lt;guid&gt;.attempts.json</c>.
/// It is the promise that Codex is launched at most once per signature, so it is written
/// BEFORE the launch and kept for good: a signature with any record - whatever came of it -
/// is not launched again. A history that cannot be read, or a record that cannot be
/// written, refuses the launch (<see cref="AttemptHistoryException"/>): not knowing is not
/// permission.
/// </summary>
public sealed class AttemptHistory
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _path;
    private readonly List<Attempt> _attempts;

    private AttemptHistory(string path, List<Attempt> attempts)
    {
        _path = path;
        _attempts = attempts;
    }

    public static string PathFor(string enhancedDir, string guid) => Path.Combine(enhancedDir, guid + ".attempts.json");

    /// <summary>The character's history: empty when there is no file, an error when there is one that cannot be read.</summary>
    public static AttemptHistory Load(string enhancedDir, string guid)
    {
        if (!ManifestWriter.IsSafeKey(guid)) throw new ArgumentException("not a GUID", nameof(guid));
        var path = PathFor(enhancedDir, guid);
        if (!File.Exists(path)) return new AttemptHistory(path, []);
        try
        {
            var file = JsonSerializer.Deserialize<Stored>(File.ReadAllText(path), Json);
            if (file?.Attempts is null || file.Guid != guid) throw new AttemptHistoryException($"{path} is not {guid}'s attempt history");
            if (file.Attempts.Any(a => string.IsNullOrEmpty(a.Signature))) throw new AttemptHistoryException($"{path} has an attempt with no signature");
            return new AttemptHistory(path, file.Attempts);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            throw new AttemptHistoryException($"{path} could not be read: {ex.Message}");
        }
    }

    public IReadOnlyList<Attempt> Attempts => _attempts;

    /// <summary>Whether anything was ever launched for the signature. Any outcome counts, unknown included.</summary>
    public bool Has(string signature) => _attempts.Any(a => a.Signature == signature);

    public Attempt? Last => _attempts.Count == 0 ? null : _attempts[^1];

    /// <summary>The last attempt that wrote a picture, if any: what is on disk, whatever was asked for since.</summary>
    public Attempt? LastWritten => _attempts.LastOrDefault(a => a.Outcome == Attempt.Written);

    /// <summary>
    /// Record a launch before it happens. Refuses a signature already recorded. Written to disk
    /// atomically before this returns; if that fails, nothing is launched.
    /// </summary>
    public Attempt Begin(Attempt attempt)
    {
        if (string.IsNullOrEmpty(attempt.Signature)) throw new ArgumentException("an attempt has a signature", nameof(attempt));
        if (Has(attempt.Signature)) throw new AttemptHistoryException($"{attempt.Signature} was already attempted");
        var started = attempt with { Outcome = Attempt.Unknown, Ended = null };
        _attempts.Add(started);
        Save();
        return started;
    }

    /// <summary>Record how the launch ended.</summary>
    public void End(string signature, string outcome, DateTime when, string? outputHash = null)
    {
        var i = _attempts.FindIndex(a => a.Signature == signature);
        if (i < 0) throw new AttemptHistoryException($"{signature} was never begun");
        _attempts[i] = _attempts[i] with { Outcome = outcome, Ended = when, OutputHash = outputHash };
        Save();
    }

    private void Save()
    {
        var guid = Path.GetFileName(_path);
        guid = guid[..^".attempts.json".Length];
        var text = JsonSerializer.Serialize(new Stored { Guid = guid, Attempts = _attempts }, Json);
        var tmp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(tmp, text);
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try { File.Delete(tmp); } catch (IOException) { }
            throw new AttemptHistoryException($"{_path} could not be written: {ex.Message}");
        }
    }

    private sealed class Stored
    {
        public string? Guid { get; set; }
        public List<Attempt>? Attempts { get; set; }
    }
}

public sealed class AttemptHistoryException(string message) : Exception(message);
