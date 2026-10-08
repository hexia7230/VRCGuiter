using System.Text.Json;

namespace VRCGuiter;

public sealed class AppSettings
{
    public string? InputDeviceId { get; set; }
    public int InputChannel { get; set; }
    public bool Exclusive { get; set; }
    public string? OutputDeviceId { get; set; }
    public string? MonitorDeviceId { get; set; }

    public int NoiseStrength { get; set; } = 60;
    public int GateThresholdDb { get; set; } = -60;
    public int ReverbWet { get; set; } = 30;
    public int ReverbRoom { get; set; } = 55;
    public int ReverbDamp { get; set; } = 50;
    public int PreDelayMs { get; set; } = 20;
    public int OutputGainDb { get; set; }

    public bool SetupDeclined { get; set; }

    public Dictionary<string, float[]> NoiseProfiles { get; set; } = new();

    public static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VRCGuiter", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch { }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}
