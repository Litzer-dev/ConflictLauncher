using System.IO;
using System.Text.Json;

namespace ConflictLauncher.Services;

public sealed class LauncherConfig
{
    private static readonly string ConfigDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ConflictLauncher");

    private static readonly string ConfigPath = Path.Combine(ConfigDir, "config.json");

    public string InstallDirectory { get; set; } = @"C:\Games\Conflict Global Terror";

    public static LauncherConfig Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                var json = File.ReadAllText(ConfigPath);
                var cfg = JsonSerializer.Deserialize<LauncherConfig>(json);
                if (cfg is not null && !string.IsNullOrWhiteSpace(cfg.InstallDirectory))
                    return cfg;
            }
        }
        catch
        {
            // Fall back to defaults if config is corrupted
        }

        return new LauncherConfig();
    }

    public void Save()
    {
        Directory.CreateDirectory(ConfigDir);
        var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(ConfigPath, json);
    }
}
