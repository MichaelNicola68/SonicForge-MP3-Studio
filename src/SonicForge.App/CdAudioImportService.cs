using System.IO;
using Microsoft.Win32.SafeHandles;
using NAudio.Lame;
using NAudio.Wave;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace SonicForge.App;

public sealed record CdImportTrack(int Number, string Title);
public sealed record CdImportProgress(int Current, int Total, string Message, double Percent);

public sealed class CdAudioImportService
{
    private readonly AlbumCoverService _coverService = new();
    private const uint GenericRead = 0x80000000;
    private const uint FileShareRead = 1;
    private const uint FileShareWrite = 2;
    private const uint OpenExisting = 3;
    private const uint IoctlReadToc = 0x00024000;
    private const uint IoctlRawRead = 0x0002403E;
    private const int AudioSectorBytes = 2352;
    private const int SectorsPerRead = 20;

    [StructLayout(LayoutKind.Sequential)]
    private struct RawReadInfo { public long DiskOffset; public uint SectorCount; public int TrackMode; }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint code, IntPtr input, uint inputSize, byte[] output, uint outputSize, out uint returned, IntPtr overlapped);

    public async Task ImportAsync(string driveRoot, string targetRoot, string artist, string album, string year, string? releaseId,
        IReadOnlyList<CdImportTrack> selected, bool createWav, bool createMp3,
        IProgress<CdImportProgress>? progress, CancellationToken token)
    {
        if (!createWav && !createMp3) throw new InvalidOperationException("Bitte WAV und/oder MP3 auswählen.");
        var safeArtist = SafeName(string.IsNullOrWhiteSpace(artist) ? "Unbekannter Interpret" : artist);
        var safeAlbum = SafeName(string.IsNullOrWhiteSpace(album) ? "Unbekanntes Album" : album);
        var albumFolder = Path.Combine(targetRoot, safeArtist, string.IsNullOrWhiteSpace(year) ? safeAlbum : $"{safeAlbum} ({SafeName(year)})");
        var wavFolder = Path.Combine(albumFolder, "WAV");
        var mp3Folder = Path.Combine(albumFolder, "MP3-320");
        if (createWav) Directory.CreateDirectory(wavFolder);
        if (createMp3) Directory.CreateDirectory(mp3Folder);

        var cover = await _coverService.FindByReleaseAsync(releaseId, token)
            ?? await _coverService.FindAsync(artist, album, token);
        if (cover is not null)
        {
            Directory.CreateDirectory(albumFolder);
            var coverExtension = cover.MimeType.Contains("png", StringComparison.OrdinalIgnoreCase) ? ".png" : ".jpg";
            await File.WriteAllBytesAsync(Path.Combine(albumFolder, "cover" + coverExtension), cover.Data, token);
            progress?.Report(new(0, selected.Count, "Albumcover gefunden und gespeichert", 0));
        }
        else
        {
            progress?.Report(new(0, selected.Count, "Kein Albumcover gefunden, Import wird fortgesetzt", 0));
        }

        var toc = ReadToc(driveRoot);
        using var device = Open(driveRoot);
        for (var i = 0; i < selected.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            var track = selected[i];
            if (!toc.TryGetValue(track.Number, out var range)) throw new InvalidOperationException($"Track {track.Number} fehlt im Inhaltsverzeichnis.");
            var stem = $"{track.Number:00} - {SafeName(track.Title)}";
            var tempWav = Path.Combine(wavFolder, stem + ".wav.part");
            var finalWav = Path.Combine(wavFolder, stem + ".wav");
            var tempMp3 = Path.Combine(mp3Folder, stem + ".part.mp3");
            var finalMp3 = Path.Combine(mp3Folder, stem + ".mp3");
            try
            {
                progress?.Report(new(i + 1, selected.Count, $"Lese Track {track.Number:00}: {track.Title}", 0));
                Directory.CreateDirectory(wavFolder);
                await RipTrackAsync(device, range.StartLba, range.EndLba, tempWav, i + 1, selected.Count, track.Title, progress, token);
                if (createMp3)
                {
                    progress?.Report(new(i + 1, selected.Count, $"Erzeuge MP3 320 kbit/s: {track.Title}", 100));
                    await EncodeMp3Async(tempWav, tempMp3, token);
                    WriteTags(tempMp3, artist, album, year, track.Title, track.Number, selected.Count, cover);
                    File.Move(tempMp3, finalMp3, true);
                }
                if (createWav) File.Move(tempWav, finalWav, true); else File.Delete(tempWav);
            }
            catch
            {
                DeleteQuiet(tempWav); DeleteQuiet(tempMp3); throw;
            }
        }
    }

    private static async Task RipTrackAsync(SafeFileHandle device, int startLba, int endLba, string path,
        int current, int total, string title, IProgress<CdImportProgress>? progress, CancellationToken token)
    {
        var sectors = endLba - startLba;
        using var writer = new WaveFileWriter(path, new WaveFormat(44100, 16, 2));
        for (var done = 0; done < sectors;)
        {
            token.ThrowIfCancellationRequested();
            var count = Math.Min(SectorsPerRead, sectors - done);
            var data = ReadSectors(device, startLba + done, count);
            await writer.WriteAsync(data.AsMemory(0, data.Length), token);
            done += count;
            progress?.Report(new(current, total, $"Lese Track {current:00}: {title}", done * 100d / sectors));
        }
    }

    private static byte[] ReadSectors(SafeFileHandle device, int lba, int count)
    {
        var info = new RawReadInfo { DiskOffset = (long)lba * 2048L, SectorCount = (uint)count, TrackMode = 2 };
        var size = Marshal.SizeOf<RawReadInfo>();
        var ptr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(info, ptr, false);
            var data = new byte[count * AudioSectorBytes];
            if (!DeviceIoControl(device, IoctlRawRead, ptr, (uint)size, data, (uint)data.Length, out var returned, IntPtr.Zero))
                throw Win32("Audiosektoren konnten nicht gelesen werden");
            if (returned != data.Length) Array.Resize(ref data, (int)returned);
            return data;
        }
        finally { Marshal.FreeHGlobal(ptr); }
    }

    private static Dictionary<int, (int StartLba, int EndLba)> ReadToc(string driveRoot)
    {
        using var device = Open(driveRoot);
        var b = new byte[804];
        if (!DeviceIoControl(device, IoctlReadToc, IntPtr.Zero, 0, b, (uint)b.Length, out var returned, IntPtr.Zero)) throw Win32("CD-Inhaltsverzeichnis konnte nicht gelesen werden");
        var first = b[2]; var last = b[3];
        var positions = new List<int>();
        for (var i = 0; i < last - first + 2; i++)
        {
            var o = 4 + i * 8;
            positions.Add(Math.Max(0, ((b[o + 5] * 60) + b[o + 6]) * 75 + b[o + 7] - 150));
        }
        var result = new Dictionary<int, (int, int)>();
        for (var i = 0; i < positions.Count - 1; i++) result[first + i] = (positions[i], positions[i + 1]);
        return result;
    }

    private static async Task EncodeMp3Async(string wav, string mp3, CancellationToken token)
    {
        await Task.Run(() => { using var reader = new WaveFileReader(wav); using var output = new LameMP3FileWriter(mp3, reader.WaveFormat, 320); reader.CopyTo(output); }, token);
    }

    private static void WriteTags(string path, string artist, string album, string year, string title, int number, int count, AlbumCoverResult? cover)
    {
        using var file = TagLib.File.Create(path);
        file.Tag.Title = title; file.Tag.Performers = new[] { artist }; file.Tag.AlbumArtists = new[] { artist }; file.Tag.Album = album;
        file.Tag.Track = (uint)number; file.Tag.TrackCount = (uint)count;
        if (uint.TryParse(year, out var y)) file.Tag.Year = y;
        if (cover is not null)
        {
            var picture = new TagLib.Picture(new TagLib.ByteVector(cover.Data)) { Type = TagLib.PictureType.FrontCover, MimeType = cover.MimeType, Description = "Front Cover" };
            file.Tag.Pictures = new TagLib.IPicture[] { picture };
        }
        file.Save();
    }

    private static SafeFileHandle Open(string driveRoot)
    {
        var letter = char.ToUpperInvariant(driveRoot.Trim()[0]);
        var h = CreateFile($@"\\.\{letter}:", GenericRead, FileShareRead | FileShareWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
        if (h.IsInvalid) throw Win32($"CD-Laufwerk {letter}: konnte nicht geöffnet werden");
        return h;
    }
    private static Exception Win32(string text) { var e = Marshal.GetLastWin32Error(); return new IOException($"{text}. Windows-Fehler {e}: {new Win32Exception(e).Message}"); }
    private static string SafeName(string value) { var invalid = Path.GetInvalidFileNameChars(); var s = new string(value.Trim().Select(c => invalid.Contains(c) ? '_' : c).ToArray()).TrimEnd('.', ' '); return string.IsNullOrWhiteSpace(s) ? "Unbenannt" : s; }
    private static void DeleteQuiet(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }
}
