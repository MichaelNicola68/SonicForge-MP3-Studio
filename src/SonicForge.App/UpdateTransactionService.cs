using System.IO;
using System.Text.Json;

namespace SonicForge.App;

public sealed record UpdateTransactionResult(bool Success, bool RollbackPerformed, string InstallRoot, string BackupRoot, string Message);

public static class UpdateTransactionService
{
    public static UpdateTransactionResult InstallTest(UpdateManifest manifest, bool simulateFailure)
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var updateRoot = Path.Combine(local, "SonicForge", "Updates");
        var installRoot = Path.Combine(updateRoot, "TestInstallation", "Current");
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var backupRoot = Path.Combine(updateRoot, "TransactionBackups", stamp);
        var stagedRoot = FindLatestStaging(updateRoot, manifest.Version);
        if (stagedRoot is null) throw new DirectoryNotFoundException("Kein vorbereitetes Staging-Paket gefunden. Phase 8c zuerst ausfuehren.");

        Directory.CreateDirectory(backupRoot);
        if (Directory.Exists(installRoot)) CopyDirectory(installRoot, Path.Combine(backupRoot, "Previous"));
        else File.WriteAllText(Path.Combine(backupRoot, "NO_PREVIOUS_INSTALL.txt"), "Vor dem Test war keine Testinstallation vorhanden.");

        try
        {
            if (Directory.Exists(installRoot)) Directory.Delete(installRoot, true);
            Directory.CreateDirectory(installRoot);
            CopyDirectory(stagedRoot, installRoot);
            File.WriteAllText(Path.Combine(installRoot, "install-state.json"), JsonSerializer.Serialize(new {
                Version = manifest.Version, Installed = DateTime.Now, TestArea = true
            }, new JsonSerializerOptions { WriteIndented = true }));

            if (simulateFailure) throw new InvalidOperationException("Erzwungener Phase-8d-Testfehler nach dem Dateiaustausch.");
            HealthCheck(installRoot, manifest.Version);
            return new UpdateTransactionResult(true, false, installRoot, backupRoot, "Kontrollierte Testinstallation erfolgreich.");
        }
        catch (Exception ex)
        {
            if (Directory.Exists(installRoot)) Directory.Delete(installRoot, true);
            var previous = Path.Combine(backupRoot, "Previous");
            if (Directory.Exists(previous)) CopyDirectory(previous, installRoot);
            File.WriteAllText(Path.Combine(backupRoot, "ROLLBACK.txt"), "Automatischer Rueckfall ausgefuehrt. Grund: " + ex.Message);
            return new UpdateTransactionResult(false, true, installRoot, backupRoot, "Fehler erkannt; vorheriger Teststand automatisch wiederhergestellt.");
        }
    }

    private static string? FindLatestStaging(string updateRoot, string version)
    {
        var stagingRoot = Path.Combine(updateRoot, "Staging");
        if (!Directory.Exists(stagingRoot)) return null;
        return Directory.GetDirectories(stagingRoot, version + "-*", SearchOption.TopDirectoryOnly)
            .OrderByDescending(Directory.GetLastWriteTimeUtc).FirstOrDefault();
    }

    private static void HealthCheck(string installRoot, string version)
    {
        if (!File.Exists(Path.Combine(installRoot, "install-state.json")))
            throw new InvalidDataException("Installationsstatus fehlt.");
        if (!Directory.GetFiles(installRoot, "UPDATE-TEST.txt", SearchOption.AllDirectories).Any())
            throw new InvalidDataException("Testpaket-Inhalt fehlt.");
        var state = File.ReadAllText(Path.Combine(installRoot, "install-state.json"));
        if (!state.Contains(version, StringComparison.Ordinal)) throw new InvalidDataException("Installierte Testversion stimmt nicht.");
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            var target = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, true);
        }
    }
}
