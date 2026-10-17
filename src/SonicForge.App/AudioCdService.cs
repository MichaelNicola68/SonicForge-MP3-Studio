using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace SonicForge.App;

public sealed record AudioCdTrack(int Number, TimeSpan Duration)
{
    public string DisplayTitle => $"Titel {Number:00}";
    public string DurationText => Duration.ToString(@"mm\:ss");
}

public sealed record AudioCdInfo(string DriveRoot, string DriveName, IReadOnlyList<AudioCdTrack> Tracks)
{
    public TimeSpan TotalDuration => TimeSpan.FromSeconds(Tracks.Sum(t => t.Duration.TotalSeconds));
    public string TotalDurationText => TotalDuration.ToString(@"hh\:mm\:ss");
}

public sealed class AudioCdService
{
    [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
    private static extern int mciSendString(string command, StringBuilder? returnValue, int returnLength, IntPtr callback);

    public IReadOnlyList<DriveInfo> GetOpticalDrives() => DriveInfo.GetDrives()
        .Where(d => d.DriveType == DriveType.CDRom)
        .OrderBy(d => d.Name)
        .ToArray();

    public AudioCdInfo ReadDisc(string driveRoot)
    {
        var letter = driveRoot.TrimEnd('\\').TrimEnd(':');
        const string alias = "sonicforgecd";
        Send($"close {alias}", allowFailure: true);
        try
        {
            Send($"open {letter}: type cdaudio alias {alias}");
            Send($"set {alias} time format milliseconds");
            var count = int.Parse(Query($"status {alias} number of tracks"));
            var tracks = new List<AudioCdTrack>();
            for (var track = 1; track <= count; track++)
            {
                var milliseconds = long.Parse(Query($"status {alias} length track {track}"));
                tracks.Add(new AudioCdTrack(track, TimeSpan.FromMilliseconds(milliseconds)));
            }
            var drive = GetOpticalDrives().FirstOrDefault(d => d.Name.StartsWith(letter, StringComparison.OrdinalIgnoreCase));
            var name = drive is null ? $"CD-Laufwerk {letter}:" : $"{drive.Name.TrimEnd('\\')} CD-Laufwerk";
            return new AudioCdInfo($"{letter}:\\", name, tracks);
        }
        finally
        {
            Send($"close {alias}", allowFailure: true);
        }
    }

    public void Eject(string driveRoot)
    {
        var letter = driveRoot.TrimEnd('\\').TrimEnd(':');
        const string alias = "sonicforgeeject";
        Send($"close {alias}", allowFailure: true);
        Send($"open {letter}: type cdaudio alias {alias}");
        try { Send($"set {alias} door open"); }
        finally { Send($"close {alias}", allowFailure: true); }
    }

    private static string Query(string command)
    {
        var buffer = new StringBuilder(512);
        var result = mciSendString(command, buffer, buffer.Capacity, IntPtr.Zero);
        if (result != 0) throw new InvalidOperationException($"Audio-CD-Befehl fehlgeschlagen ({result}): {command}");
        return buffer.ToString().Trim();
    }

    private static void Send(string command, bool allowFailure = false)
    {
        var result = mciSendString(command, null, 0, IntPtr.Zero);
        if (result != 0 && !allowFailure) throw new InvalidOperationException($"Audio-CD-Befehl fehlgeschlagen ({result}): {command}");
    }
}
