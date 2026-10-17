using System.IO;
using System.Text.Json;
namespace SonicForge.App;
public sealed class AppSettings
{
    public string OutputFolder { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), "SonicForge Aufnahmen");
    public string PreferredRecordingDeviceId { get; set; } = "";
    public double StartThresholdDb { get; set; } = -38;
    public double StopThresholdDb { get; set; } = -45;
    public double StartConfirmSeconds { get; set; } = 0.4;
    public double StopSilenceSeconds { get; set; } = 4;
    public double PreRollSeconds { get; set; } = 1.5;
    public bool AutomaticThresholdsEnabled { get; set; } = true;
    public bool OpenTagEditorAfterRecording { get; set; } = true;
    public bool AutoLoadCover { get; set; } = true;
    public bool AutoMetadata { get; set; } = true;
    public string MetadataSource { get; set; } = "Automatisch";
    public string FileNamePattern { get; set; } = "{Interpret} - {Titel}";
    public static string DataDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SonicForge MP3 Studio", "Data");
    public static string SettingsPath => Path.Combine(DataDirectory, "settings.json");
    public void Save(){Directory.CreateDirectory(DataDirectory);File.WriteAllText(SettingsPath,JsonSerializer.Serialize(this,new JsonSerializerOptions{WriteIndented=true}));}
    public static AppSettings Load(){try{if(File.Exists(SettingsPath))return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath))??new();}catch{}return new();}
    public static AppSettings Reset(){var s=new AppSettings();s.Save();return s;}
}
