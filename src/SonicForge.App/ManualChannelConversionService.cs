using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using System.IO;

namespace SonicForge.App;

public static class ManualChannelConversionService
{
    public static async Task ApplyAsync(string wavPath, string channelMode)
    {
        if (channelMode == "Quelle unverändert") return;
        if (!File.Exists(wavPath)) throw new FileNotFoundException("WAV-Aufnahme nicht gefunden.", wavPath);

        var tempPath = wavPath + ".channel.partial.wav";
        try
        {
            await Task.Run(() =>
            {
                using var reader = new AudioFileReader(wavPath);
                ISampleProvider source = reader;
                ISampleProvider output;

                if (channelMode == "Mono")
                {
                    output = source.WaveFormat.Channels switch
                    {
                        1 => source,
                        2 => new StereoToMonoSampleProvider(source) { LeftVolume = 0.5f, RightVolume = 0.5f },
                        _ => FirstTwoChannelsToMono(source)
                    };
                }
                else if (channelMode == "Stereo")
                {
                    output = source.WaveFormat.Channels switch
                    {
                        1 => new MonoToStereoSampleProvider(source),
                        2 => source,
                        _ => FirstTwoChannels(source)
                    };
                }
                else
                {
                    throw new InvalidOperationException("Unbekannte Kanalauswahl: " + channelMode);
                }

                WaveFileWriter.CreateWaveFile16(tempPath, output);
            });

            if (!File.Exists(tempPath) || new FileInfo(tempPath).Length <= 44)
                throw new InvalidDataException("Die Kanalumwandlung hat keine gültige WAV-Datei erzeugt.");

            var backupPath = wavPath + ".channel.backup";
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

    private static ISampleProvider FirstTwoChannels(ISampleProvider source)
    {
        var mux = new MultiplexingSampleProvider(new[] { source }, 2);
        mux.ConnectInputToOutput(0, 0);
        mux.ConnectInputToOutput(1, 1);
        return mux;
    }

    private static ISampleProvider FirstTwoChannelsToMono(ISampleProvider source)
    {
        var stereo = FirstTwoChannels(source);
        return new StereoToMonoSampleProvider(stereo) { LeftVolume = 0.5f, RightVolume = 0.5f };
    }
}
