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
    /// <summary>The "restart the game once" notice for a new addon folder has been dismissed.</summary>
    public bool FirstFolderNoticeShown { get; init; }

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static string DefaultDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AltStableCompanion");

    public static Settings Load(string dir)
    {
        try
        {
            var path = Path.Combine(dir, "settings.json");
            return File.Exists(path)
                ? JsonSerializer.Deserialize<Settings>(File.ReadAllText(path), Json) ?? new Settings()
                : new Settings();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
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
            catch (IOException)
            {
                // A log that cannot be written must not take the app with it.
            }
        }
    }
}
