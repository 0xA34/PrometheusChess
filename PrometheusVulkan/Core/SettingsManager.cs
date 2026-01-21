using System.Text.Json;

namespace PrometheusVulkan.Core;

public class GameSettings
{
    // It's chess, why do you even need uncapped frames?
    // Of course users can toggle on or off, but it's not gonna change anything.
    // I would recommend just keep it vsync, because uncapped frames are just gonna consume more power.
    public bool VSync { get; set; } = true;
    public bool Fullscreen { get; set; } = false;
    public bool ShowDebugStats { get; set; } = false;
    public string LastUsername { get; set; } = "";
    public float MasterVolume { get; set; } = 1.0f;
    public bool ShowLegalMoveHints { get; set; } = true;
}

public static class SettingsManager
{
    private static readonly string SettingsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings.json");

    public static GameSettings Instance { get; private set; } = new();

    static SettingsManager()
    {
        Load();
    }

    public static void Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                var settings = JsonSerializer.Deserialize<GameSettings>(json);
                if (settings != null)
                {
                    Instance = settings;
                    Console.WriteLine("[SettingsManager] Settings loaded successfully");
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SettingsManager] Failed to load settings: {ex.Message}");
        }

        Instance = new GameSettings();
    }

    public static void Save()
    {
        try
        {
            var options = new JsonSerializerOptions { WriteIndented = true };
            var json = JsonSerializer.Serialize(Instance, options);
            File.WriteAllText(SettingsPath, json);
            Console.WriteLine("[SettingsManager] Settings saved successfully");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SettingsManager] Failed to save settings: {ex.Message}");
        }
    }
}
