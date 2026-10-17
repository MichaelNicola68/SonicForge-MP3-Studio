using System.Reflection;

namespace SonicForge.App;

public static class VersionInfoService
{
    public const string ProductName = "SonicForge MP3 Studio";
    public const string ReleaseChannel = "Release Candidate";
    public const string UpdateChannel = "stable";
    public const string DevelopmentVersion = "1.0.0-rc.3-dev";

    public static string CurrentVersion
    {
        get
        {
            var informational = Assembly.GetEntryAssembly()?
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                .InformationalVersion;
            if (!string.IsNullOrWhiteSpace(informational))
                return informational.Split('+')[0];
            return Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? DevelopmentVersion;
        }
    }

    public static string DisplayVersion => $"{ProductName} {CurrentVersion}";
    public static string Platform => Environment.Is64BitProcess ? "Windows x64" : "Windows x86";
    public static string UpdateStatus => "Phase 8a: Updateinformationen bereit. Online-Pruefung folgt in Phase 8b.";
}
