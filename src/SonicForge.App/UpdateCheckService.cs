using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SonicForge.App;

public sealed record UpdateManifest(
    string Product,
    string Version,
    string Channel,
    string Platform,
    string ReleaseNotes,
    string PackageFile,
    string Sha256);

public sealed record UpdateCheckResult(
    bool UpdateAvailable,
    string InstalledVersion,
    UpdateManifest Manifest,
    string SourcePath);

public static class UpdateCheckService
{
    public static readonly string DefaultSourcePath = @"G:\SonicForge-Updatequelle\update.json";

    public static UpdateCheckResult Check(string? sourcePath = null)
    {
        var path = string.IsNullOrWhiteSpace(sourcePath) ? DefaultSourcePath : sourcePath;
        if (!File.Exists(path))
            throw new FileNotFoundException("Die lokale Updatequelle wurde nicht gefunden.", path);

        var json = File.ReadAllText(path);
        var manifest = JsonSerializer.Deserialize<UpdateManifest>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("Die Updatequelle ist leer oder ungueltig.");

        Validate(manifest);
        var installed = NormalizeVersion(VersionInfoService.CurrentVersion);
        var available = NormalizeVersion(manifest.Version);
        return new UpdateCheckResult(CompareVersions(available, installed) > 0,
            VersionInfoService.CurrentVersion, manifest, path);
    }

    private static void Validate(UpdateManifest manifest)
    {
        if (!string.Equals(manifest.Product, VersionInfoService.ProductName, StringComparison.Ordinal))
            throw new InvalidDataException("Die Updatequelle gehoert nicht zu SonicForge MP3 Studio.");
        if (!string.Equals(manifest.Channel, VersionInfoService.UpdateChannel, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Der Update-Kanal stimmt nicht mit dem installierten Kanal ueberein.");
        if (!string.Equals(manifest.Platform, "win-x64", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Das Update ist nicht fuer Windows x64 bestimmt.");
        if (!Regex.IsMatch(manifest.Version ?? "", @"^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$"))
            throw new InvalidDataException("Die Versionsnummer der Updatequelle ist ungueltig.");
        if (!string.IsNullOrWhiteSpace(manifest.Sha256) &&
            !Regex.IsMatch(manifest.Sha256, "^[0-9A-Fa-f]{64}$"))
            throw new InvalidDataException("Die SHA256-Angabe der Updatequelle ist ungueltig.");
    }

    private static string NormalizeVersion(string value)
        => value.Split('+')[0].Replace("-dev", "", StringComparison.OrdinalIgnoreCase);

    private static int CompareVersions(string left, string right)
    {
        static (int major, int minor, int patch, int preRank, int preNumber) Parse(string value)
        {
            var parts = value.Split('-', 2);
            var core = parts[0].Split('.').Select(int.Parse).ToArray();
            var rank = parts.Length == 1 ? 100 : parts[1].StartsWith("rc.", StringComparison.OrdinalIgnoreCase) ? 50 : 10;
            var number = 0;
            if (parts.Length > 1)
            {
                var digits = new string(parts[1].Where(char.IsDigit).ToArray());
                int.TryParse(digits, out number);
            }
            return (core[0], core[1], core[2], rank, number);
        }
        return Parse(left).CompareTo(Parse(right));
    }
}
