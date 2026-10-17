using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace SonicForge.App;

public sealed record UpdatePreparationResult(string PackagePath, string Sha256, string BackupPath, string StagingPath);

public static class UpdatePreparationService
{
    public static UpdatePreparationResult Prepare(UpdateManifest manifest)
    {
        var sourceRoot = Path.GetDirectoryName(UpdateCheckService.DefaultSourcePath)
            ?? throw new InvalidOperationException("Updatequellordner konnte nicht bestimmt werden.");
        if (string.IsNullOrWhiteSpace(manifest.PackageFile))
            throw new InvalidDataException("Im Updatemanifest fehlt der Paketdateiname.");
        if (string.IsNullOrWhiteSpace(manifest.Sha256))
            throw new InvalidDataException("Im Updatemanifest fehlt die SHA256-Pruefsumme.");

        var packagePath = Path.GetFullPath(Path.Combine(sourceRoot, manifest.PackageFile));
        if (!packagePath.StartsWith(Path.GetFullPath(sourceRoot), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Der Paketpfad liegt ausserhalb der Updatequelle.");
        if (!File.Exists(packagePath)) throw new FileNotFoundException("Das Updatepaket wurde nicht gefunden.", packagePath);

        using var stream = File.OpenRead(packagePath);
        var actual = Convert.ToHexString(SHA256.HashData(stream));
        if (!string.Equals(actual, manifest.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("SHA256-Pruefung fehlgeschlagen. Das Paket wird nicht vorbereitet.");

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var root = Path.Combine(local, "SonicForge", "Updates");
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var staging = Path.Combine(root, "Staging", manifest.Version + "-" + stamp);
        var backup = Path.Combine(root, "Backups", "vor-" + manifest.Version + "-" + stamp);
        Directory.CreateDirectory(staging);
        Directory.CreateDirectory(backup);

        ZipFile.ExtractToDirectory(packagePath, staging, true);
        var currentInfo = new {
            Version = VersionInfoService.CurrentVersion,
            Created = DateTime.Now,
            Note = "Phase 8c Sicherungsmarke. Programmdateien wurden nicht ersetzt."
        };
        File.WriteAllText(Path.Combine(backup, "Sicherung.json"),
            JsonSerializer.Serialize(currentInfo, new JsonSerializerOptions { WriteIndented = true }));
        File.Copy(UpdateCheckService.DefaultSourcePath, Path.Combine(backup, "update.json"), true);
        return new UpdatePreparationResult(packagePath, actual, backup, staging);
    }
}
