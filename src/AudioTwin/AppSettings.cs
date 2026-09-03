using System.Text.Json;

namespace AudioTwin;

/// <summary>
/// Stored in %AppData%\AudioTwin\settings.json. Failure to read or
/// write settings is never fatal.
/// </summary>
public sealed class AppSettings
{
    public string? Output1DeviceId { get; set; }
    public string? Output2DeviceId { get; set; }
    public int Volume1 { get; set; } = 100;
    public int Volume2 { get; set; } = 100;
    public int Delay1Ms { get; set; }
    public int Delay2Ms { get; set; }
    public bool StartRoutingOnLaunch { get; set; }
    public bool StartWithWindows { get; set; }

    /// <summary>Launch straight into the notification area, with no window.</summary>
    public bool StartMinimized { get; set; }

    /// <summary>The window's X button hides to the tray instead of quitting.</summary>
    public bool CloseToTray { get; set; } = true;

    /// <summary>The "it is still running down here" balloon is only shown once.</summary>
    public bool TrayHintShown { get; set; }

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static string DirectoryPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AudioTwin");

    public static string FilePath => Path.Combine(DirectoryPath, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new AppSettings();
            var json = File.ReadAllText(FilePath);
            return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(DirectoryPath);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Options));
        }
        catch
        {
            // Read-only profile, roaming issues, ... - not worth bothering the user.
        }
    }
}
