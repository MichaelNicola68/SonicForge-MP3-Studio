using NAudio.Wave;
using System.IO;

namespace SonicForge.App;

public static class ManualQualityConversionService
{
    public static (int SampleRate, int Bits) Profile(string qualityProfile) => qualityProfile switch
    {
        "Standard" => (44100, 16),
        "Hoch" => (48000, 24),
        "Archiv" => (48000, 24),
        _ => (44100, 16)
    };

    public static async Task ApplyAsync(string wavPath, string qualityProfile)
    {
        if (!File.Exists(wavPath)) throw new FileNotFoundException("WAV-Aufnahme nicht gefunden.", wavPath);
        var (sampleRate, bits) = Profile(qualityProfile);
        var tempPath = wavPath + ".quality.partial.wav";
        var backupPath = wavPath + ".quality.backup";

        try
        {
            await Task.Run(() =>
            {
                using var reader = new WaveFileReader(wavPath);
                var input = reader.WaveFormat.Encoding == WaveFormatEncoding.Pcm
                    ? (IWaveProvider)reader
                    : new WaveFloatTo16Provider(reader);
                var target = new WaveFormat(sampleRate, bits, input.WaveFormat.Channels);
                using var resampler = new MediaFoundationResampler(input, target) { ResamplerQuality = 60 };
                WaveFileWriter.CreateWaveFile(tempPath, resampler);
            });

            using (var check = new WaveFileReader(tempPath))
            {
                if (check.Length <= 0) throw new InvalidDataException("Die Qualitätsumwandlung hat keine Audiodaten erzeugt.");
                if (check.WaveFormat.SampleRate != sampleRate || check.WaveFormat.BitsPerSample != bits)
                    throw new InvalidDataException($"Unerwartetes WAV-Format: {check.WaveFormat.SampleRate} Hz / {check.WaveFormat.BitsPerSample} Bit.");
            }

            if (File.Exists(backupPath)) File.Delete(backupPath);
            File.Replace(tempPath, wavPath, backupPath);
            if (File.Exists(backupPath)) File.Delete(backupPath);
        }
        catch
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
            throw;
        }
    }
}
