using System.Text.Json;
using System.IO;

namespace SonicForge.App;

public sealed class SoundLabSettings
{
    public static readonly float[] Frequencies = { 31, 62, 125, 250, 500, 1000, 2000, 4000, 8000, 16000 };
    public float[] Gains { get; set; } = new float[10];
    public bool Enabled { get; set; } = true;
    public bool AutoHeadroom { get; set; } = true;
    public float ManualPreampDb { get; set; }
    public string PresetName { get; set; } = "Neutral";

    public float EffectivePreampDb => AutoHeadroom
        ? Math.Min(0, -Gains.Max())
        : Math.Clamp(ManualPreampDb, -12, 0);

    public event EventHandler? Changed;

    public void NotifyChanged()
    {
        Save();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void ApplyPreset(string name)
    {
        PresetName = name;
        Gains = name switch
        {
            "SonicForge Jukebox" => new float[] { 3, 3, 2, 1, 0, 0, 1, 2, 1, 1 },
            "Rock Live" => new float[] { 3, 2, 1, 0, -1, 0, 2, 3, 2, 1 },
            "Bass ohne Dröhnen" => new float[] { 4, 3, 1, -2, -1, 0, 0, 1, 1, 0 },
            "Sprache klar" => new float[] { -5, -3, -2, -1, 0, 2, 3, 2, 0, -1 },
            "Nachtmodus" => new float[] { -5, -4, -2, 0, 1, 2, 2, 1, 0, -1 },
            "Kopfhörer entspannt" => new float[] { 2, 2, 1, 0, 0, 0, -1, -2, -1, 1 },
            _ => new float[10]
        };
        NotifyChanged();
    }

    public void Save()
    {
        Directory.CreateDirectory(DataDirectory);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }

    public static SoundLabSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
                return JsonSerializer.Deserialize<SoundLabSettings>(File.ReadAllText(SettingsPath)) ?? new();
        }
        catch { }
        return new SoundLabSettings();
    }

    private static string DataDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SonicForge MP3 Studio", "Data");
    private static string SettingsPath => Path.Combine(DataDirectory, "soundlab.json");
}
