using System.Text.Json;

namespace VRCGuiter;

/// <summary>%AppData%\VRCGuiter\settings.json に保存する設定。</summary>
public sealed class AppSettings
{
    public string? InputDeviceId { get; set; }
    /// <summary>0 = ミックス, 1 = L のみ, 2 = R のみ</summary>
    public int InputChannel { get; set; }
    public bool Exclusive { get; set; }
    public string? OutputDeviceId { get; set; }
    public string? MonitorDeviceId { get; set; }

    public int NoiseStrength { get; set; } = 60;     // 0..100
    public int GateThresholdDb { get; set; } = -60;  // -100(オフ)..-30
    public int ReverbWet { get; set; } = 30;         // 0..100
    public int ReverbRoom { get; set; } = 55;        // 0..100
    public int ReverbDamp { get; set; } = 50;        // 0..100
    public int PreDelayMs { get; set; } = 20;        // 0..150
    public int OutputGainDb { get; set; }            // -24..24

    public bool SetupDeclined { get; set; }

    /// <summary>キー = "deviceId|sampleRate"、値 = パワースペクトル（bins）</summary>
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
        catch { /* 壊れていたら初期値 */ }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* 保存失敗は致命的ではない */ }
    }
}
