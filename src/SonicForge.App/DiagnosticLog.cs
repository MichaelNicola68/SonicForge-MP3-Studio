using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;

namespace SonicForge.App;

public static class DiagnosticLog
{
    private static readonly object Sync = new();
    private static bool _initialized;
    public static string LogFolder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SonicForge", "Logs");

    public static void Initialize()
    {
        lock (Sync)
        {
            if (_initialized) return;
            _initialized = true;
            Directory.CreateDirectory(LogFolder);
            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
                Error("AppDomain", e.ExceptionObject as Exception ?? new Exception(e.ExceptionObject?.ToString()));
            TaskScheduler.UnobservedTaskException += (_, e) =>
            {
                Error("TaskScheduler", e.Exception);
                e.SetObserved();
            };
            if (System.Windows.Application.Current is not null)
                System.Windows.Application.Current.DispatcherUnhandledException += OnDispatcherException;
            CleanOldLogs(30);
            Info("Application", "SonicForge gestartet");
        }
    }

    private static void OnDispatcherException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Error("Dispatcher", e.Exception);
    }

    public static void Info(string area, string message) => Write("INFO", area, message, null);
    public static void Warning(string area, string message) => Write("WARN", area, message, null);
    public static void Error(string area, Exception? exception, string? message = null) =>
        Write("ERROR", area, message ?? exception?.Message ?? "Unbekannter Fehler", exception);

    private static void Write(string level, string area, string message, Exception? exception)
    {
        try
        {
            Directory.CreateDirectory(LogFolder);
            var safe = message.Replace("\r", " ").Replace("\n", " ");
            var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz}|{level}|{area}|{safe}";
            if (exception is not null)
                line += $"|{exception.GetType().FullName}|{exception.StackTrace?.Replace("\r", " ").Replace("\n", " ")}";
            lock (Sync)
                File.AppendAllText(CurrentFile(), line + Environment.NewLine, new UTF8Encoding(false));
        }
        catch { }
    }

    private static string CurrentFile() => Path.Combine(LogFolder, $"SonicForge-{DateTime.Now:yyyy-MM-dd}.log");

    private static void CleanOldLogs(int days)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(LogFolder, "SonicForge-*.log"))
                if (File.GetLastWriteTimeUtc(file) < DateTime.UtcNow.AddDays(-days)) File.Delete(file);
        }
        catch { }
    }
}
