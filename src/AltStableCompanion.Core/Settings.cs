using System.Text.Json;

namespace AltStableCompanion.Core;

/// <summary>
/// What the app remembers, in <c>%APPDATA%\AltStableCompanion\settings.json</c>. Anything
/// missing or unreadable falls back to the defaults - settings are a convenience, never a
/// reason not to start.
/// </summary>
public sealed record Settings
{
    /// <summary>A flavour folder the player chose; null means detect it.</summary>
    public string? WowFlavorDir { get; init; }
    public bool KeepScreenshots { get; init; }
    public bool Paused { get; init; }

    /// <summary>
    /// The player has been told what happens to their screenshots and has said "Start
    /// watching". Until then nothing is converted. Written down here, and not read off whether
    /// a settings file exists: pausing from the tray writes one too.
    /// </summary>
    public bool Started { get; init; }

    /// <summary>
    /// What the window is made of: one of the addon's skins, so that the two match. A name that
    /// is not one of them - a file from a later version, a hand edit - reads as the default.
    /// </summary>
    public string Skin
    {
        get;
        init => field = Skins.Normalize(value);
    } = Skins.Clear;

    /// <summary>
    /// Which folder the player chose is not known: the settings could not be read at some
    /// start, and they have not chosen since. While this is set nothing is detected for them -
    /// it is cleared by Browse and by Detect again, and by nothing else.
    /// </summary>
    public bool InstallUnknown { get; init; }

    /// <summary>
    /// Enhanced portraits (docs/PORTRAIT-CONTRACT.md, section 3): off unless the player turned
    /// it on, because it sends a picture to a service. The minimum level is the armory's rule:
    /// a level-one alt is not worth a generation. The style is one of <see cref="EnhanceStyles"/>.
    /// Model, effort and timeout are in the file only: tuning, not a choice the window offers.
    /// </summary>
    public bool Enhance { get; init; }

    public int EnhanceMinLevel
    {
        get;
        init => field = Math.Clamp(value, 1, 60);
    } = 10;

    public string EnhanceStyle
    {
        get;
        init => field = EnhanceStyles.Normalize(value);
    } = EnhanceStyles.WowLike;

    public string EnhanceModel
    {
        get;
        init => field = string.IsNullOrWhiteSpace(value) ? "gpt-6-astra" : value.Trim();
    } = "gpt-6-astra";

    public string EnhanceEffort
    {
        get;
        init => field = value?.Trim().ToLowerInvariant() is "low" or "medium" or "high" ? value.Trim().ToLowerInvariant() : "low";
    } = "low";

    public int EnhanceTimeoutSeconds
    {
        get;
        init => field = Math.Clamp(value, 60, 1800);
    } = 360;

    /// <summary>
    /// Flavour folders that owe the player the "restart the game once" notice. A folder is
    /// added BEFORE the pass that may create its AltStableCutouts addon and removed when the
    /// notice is dismissed, so the notice survives the app being closed - or killed - in
    /// between. The notice shows once the addon's .toc exists. An install whose addon was
    /// already there when the app first met it is never added.
    /// </summary>
    public IReadOnlyList<string> RestartNoticeInstalls
    {
        get;
        // A file can say null, or hold one: neither is a reason not to start.
        init => field = [.. (value ?? []).Where(d => !string.IsNullOrWhiteSpace(d))];
    } = [];

    public bool OwesRestartNotice(string flavorDir) =>
        RestartNoticeInstalls.Contains(flavorDir, StringComparer.OrdinalIgnoreCase);

    public Settings WithRestartNotice(string flavorDir, bool owed) => this with
    {
        RestartNoticeInstalls = owed
            ? [.. RestartNoticeInstalls.Where(d => !Same(d, flavorDir)), flavorDir]
            : [.. RestartNoticeInstalls.Where(d => !Same(d, flavorDir))],
    };

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    // A record compares a list by reference; two settings with the same folders are the same.
    public bool Equals(Settings? other) =>
        other is not null
        && WowFlavorDir == other.WowFlavorDir
        && KeepScreenshots == other.KeepScreenshots
        && Paused == other.Paused
        && Started == other.Started
        && Skin == other.Skin
        && InstallUnknown == other.InstallUnknown
        && Enhance == other.Enhance
        && EnhanceMinLevel == other.EnhanceMinLevel
        && EnhanceStyle == other.EnhanceStyle
        && EnhanceModel == other.EnhanceModel
        && EnhanceEffort == other.EnhanceEffort
        && EnhanceTimeoutSeconds == other.EnhanceTimeoutSeconds
        && RestartNoticeInstalls.SequenceEqual(other.RestartNoticeInstalls);

    public override int GetHashCode() =>
        HashCode.Combine(WowFlavorDir, KeepScreenshots, Paused, Started, Skin, InstallUnknown, Enhance, RestartNoticeInstalls.Count);

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    // Whether the file names the property at all, whatever its value.
    private static bool Has(string json, string property)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty(property, out _);
    }

    public static string DefaultDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AltStableCompanion");

    public static Settings Load(string dir) => Load(dir, out _);

    /// <summary>
    /// The settings, and - when there IS a file and it could not be read - what was wrong with
    /// it. The defaults that come back then are not what the player chose: whoever acts on
    /// them has to know that. No file at all is a first start, and no problem.
    /// </summary>
    public static Settings Load(string dir, out string? problem)
    {
        problem = null;
        var path = Path.Combine(dir, "settings.json");
        try
        {
            if (!File.Exists(path)) return new Settings();
            var text = File.ReadAllText(path);
            var settings = JsonSerializer.Deserialize<Settings>(text, Json) ?? new Settings();
            // A file from before "Started" existed: whoever chose a folder, or is owed a restart
            // notice, has used the app, and their captures must not stop converting until they
            // find a card in a window they may never open. A file that SAYS Started is false
            // is this version's, written before the card was answered - Browse writes one -
            // and that answer is still owed.
            if (!settings.Started && !Has(text, nameof(Started))
                && (settings.WowFlavorDir is not null || settings.RestartNoticeInstalls.Count > 0))
            {
                settings = settings with { Started = true };
            }
            return settings;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            problem = $"{path} could not be read: {ex.Message}";
            return new Settings();
        }
    }

    public void Save(string dir)
    {
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "settings.json");
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(this, Json));
        File.Move(path + ".tmp", path, overwrite: true);
    }
}

/// <summary>
/// The addon's skins (Skin.lua): what the window is made of. The looks themselves - the
/// colours, the blur - are the shell's; this is only which one was chosen.
/// </summary>
public static class Skins
{
    public const string Clear = "clear";
    public const string Smoked = "smoked";
    public const string Flat = "flat";

    public static readonly IReadOnlyList<string> All = [Clear, Smoked, Flat];

    public static string Normalize(string? name) =>
        name is not null && All.FirstOrDefault(s => string.Equals(s, name.Trim(), StringComparison.OrdinalIgnoreCase)) is { } known
            ? known
            : Clear;
}

/// <summary>A plain text log beside the settings, rolled over at 1 MB. No logging library.</summary>
public sealed class Log(string dir)
{
    private const long MaxBytes = 1024 * 1024;
    private readonly Lock _gate = new();

    public string Path { get; } = System.IO.Path.Combine(dir, "log.txt");

    public void Write(string message)
    {
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
                if (File.Exists(Path) && new FileInfo(Path).Length > MaxBytes)
                {
                    File.Move(Path, Path + ".old", overwrite: true);
                }
                File.AppendAllText(Path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}{Environment.NewLine}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A log that cannot be written must not take the app with it.
            }
        }
    }
}
