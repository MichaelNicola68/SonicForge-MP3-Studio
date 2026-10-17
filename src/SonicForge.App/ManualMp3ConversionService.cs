using NAudio.Lame;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using System.IO;

namespace SonicForge.App;

public static class ManualMp3ConversionService
{
    public static async Task<string> ConvertAsync(string wavPath, int bitRate)
    {
        if (string.IsNullOrWhiteSpace(wavPath) || !File.Exists(wavPath))
            throw new FileNotFoundException("Die WAV-Aufnahme wurde nicht gefunden.", wavPath);

        var mp3Path = UniquePath(Path.ChangeExtension(wavPath, ".mp3"));
        var tempPath = mp3Path + ".partial";
        try
        {
            await Task.Run(() =>
            {
                using var reader = new AudioFileReader(wavPath);
                IWaveProvider pcm16;

                if (reader.WaveFormat.Channels <= 2)
                {
                    pcm16 = reader.ToWaveProvider16();
                }
                else
                {
                    var stereo = new MultiplexingSampleProvider(new ISampleProvider[] { reader }, 2);
                    stereo.ConnectInputToOutput(0, 0);
                    stereo.ConnectInputToOutput(1, 1);
                    pcm16 = stereo.ToWaveProvider16();
                }

                using var writer = new LameMP3FileWriter(tempPath, pcm16.WaveFormat, bitRate);
                var buffer = new byte[64 * 1024];
                int read;
                while ((read = pcm16.Read(buffer, 0, buffer.Length)) > 0)
                    writer.Write(buffer, 0, read);
            });

            if (!File.Exists(tempPath) || new FileInfo(tempPath).Length == 0)
                throw new InvalidDataException("Die MP3-Datei wurde nicht korrekt erzeugt.");

            File.Move(tempPath, mp3Path);
            return mp3Path;
        }
        catch
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
            throw;
        }
    }

    private static string UniquePath(string path)
    {
        if (!File.Exists(path)) return path;
        var folder = Path.GetDirectoryName(path)!;
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        var n = 2;
        string candidate;
        do candidate = Path.Combine(folder, $"{name}-{n++}{ext}"); while (File.Exists(candidate));
        return candidate;
    }
}
