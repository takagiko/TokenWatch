using System.IO;
using System.Text.Json;

namespace TokenWatch.App;

/// <summary>User-editable settings persisted to %LOCALAPPDATA%\TokenWatch\settings.json.</summary>
public sealed class AppSettings
{
    public double PollIntervalMinutes { get; set; } = 2;
    public int WarnPercent { get; set; } = 50;
    public int CriticalPercent { get; set; } = 80;
    public bool StartWithWindows { get; set; }
    public bool NotificationsEnabled { get; set; } = true;

    public static string Folder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TokenWatch");

    private static string FilePath => Path.Combine(Folder, "settings.json");

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath));
                if (s is not null) return s.Normalized();
            }
        }
        catch { /* fall back to defaults */ }

        var def = new AppSettings();
        def.Save(); // write a starter file the user can edit
        return def;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOpts));
        }
        catch { /* non-fatal */ }
    }

    private AppSettings Normalized()
    {
        PollIntervalMinutes = Math.Clamp(PollIntervalMinutes, 0.5, 60);
        WarnPercent = Math.Clamp(WarnPercent, 1, 99);
        CriticalPercent = Math.Clamp(CriticalPercent, WarnPercent + 1, 100);
        return this;
    }
}
