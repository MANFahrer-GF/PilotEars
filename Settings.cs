using System.IO;
using System.Text.Json;

namespace PilotEars;

public sealed class Settings
{
    public string? InputDeviceId { get; set; }
    public string? OutputDeviceId { get; set; }
    public int LatencyMs { get; set; } = 30;
    public float NormalizerTargetDb { get; set; } = -18f;
    public float LimiterCeilingDb { get; set; } = -1f;
    public float LimiterReleaseMs { get; set; } = 50f;
    public float LimiterLookaheadMs { get; set; } = 5f;
    public float Pan { get; set; } = 0f;

    public bool DuckEnabled { get; set; } = true;
    public float DuckAmount { get; set; } = 0.5f;
    public float DuckThreshold { get; set; } = 0.02f;
    public int DuckAttackMs { get; set; } = 30;
    public int DuckReleaseMs { get; set; } = 400;

    public string Language { get; set; } = "EN";

    // Mixer-based Discord routing: capture Discord from its device and mix it
    // into PilotEars's own output. Empty = mixer disabled.
    public string? DiscordSourceDeviceId { get; set; }
    public float DiscordMixLevel { get; set; } = 1.0f;

    public List<PresetData> CustomPresets { get; set; } = new();

    private static string FilePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                     "PilotEars", "settings.json");

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                return JsonSerializer.Deserialize<Settings>(json) ?? new Settings();
            }
        }
        catch { }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(FilePath)!;
            Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(FilePath, json);
        }
        catch { }
    }
}
