using NAudio.CoreAudioApi;
using NAudio.Wave;
using System.IO;

namespace SonicForge.App;

public sealed record RecordingDevice(string Id, string Name, bool IsLoopback)
{
    public override string ToString() => Name;
}

public sealed class RecordingLevelEventArgs(float left, float right) : EventArgs
{
    public float Left { get; } = left;
    public float Right { get; } = right;
}

public sealed class RecordingService : IDisposable
{
    private IWaveIn? _capture;
    private WaveFileWriter? _writer;
    private volatile bool _paused;
    private string? _outputPath;
    private bool _deleteAfterStop;
    private DateTime _startedUtc;
    private TimeSpan _pausedTotal;
    private DateTime? _pauseStartedUtc;

    public event EventHandler<RecordingLevelEventArgs>? LevelChanged;
    public event EventHandler<StoppedEventArgs>? RecordingStopped;
    public bool IsRecording => _capture is not null;
    public bool IsPaused => _paused;
    public string? OutputPath => _outputPath;
    public TimeSpan Elapsed => !IsRecording ? TimeSpan.Zero :
        (DateTime.UtcNow - _startedUtc - _pausedTotal - (_pauseStartedUtc is null ? TimeSpan.Zero : DateTime.UtcNow - _pauseStartedUtc.Value));

    public static IReadOnlyList<RecordingDevice> GetDevices()
    {
        using var enumerator = new MMDeviceEnumerator();
        var result = new List<RecordingDevice>();
        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
            result.Add(new(device.ID, "Mikrofon / Line-In: " + device.FriendlyName, false));
        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            result.Add(new(device.ID, "Systemton: " + device.FriendlyName, true));
        return result;
    }

    public void Start(RecordingDevice device, string outputPath)
    {
        if (IsRecording) throw new InvalidOperationException("Es lÃ¤uft bereits eine Aufnahme.");
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        using var enumerator = new MMDeviceEnumerator();
        var endpoint = enumerator.GetDevice(device.Id);
        _capture = device.IsLoopback ? new WasapiLoopbackCapture(endpoint) : new WasapiCapture(endpoint);
        _writer = new WaveFileWriter(outputPath, _capture.WaveFormat);
        _outputPath = outputPath;
        _deleteAfterStop = false;
        _paused = false;
        _pausedTotal = TimeSpan.Zero;
        _pauseStartedUtc = null;
        _startedUtc = DateTime.UtcNow;
        _capture.DataAvailable += Capture_DataAvailable;
        _capture.RecordingStopped += Capture_RecordingStopped;
        _capture.StartRecording();
    }

    public void Pause()
    {
        if (!IsRecording || _paused) return;
        _paused = true;
        _pauseStartedUtc = DateTime.UtcNow;
    }

    public void Resume()
    {
        if (!IsRecording || !_paused) return;
        if (_pauseStartedUtc is not null) _pausedTotal += DateTime.UtcNow - _pauseStartedUtc.Value;
        _pauseStartedUtc = null;
        _paused = false;
    }

    public void Stop()
    {
        if (!IsRecording) return;
        _capture!.StopRecording();
    }

    public void Cancel()
    {
        if (!IsRecording) return;
        _deleteAfterStop = true;
        Stop();
    }

    private void Capture_DataAvailable(object? sender, WaveInEventArgs e)
    {
        if (_paused)
        {
            LevelChanged?.Invoke(this, new(0, 0));
            return;
        }
        _writer?.Write(e.Buffer, 0, e.BytesRecorded);
        var (left, right) = ReadPeak(e.Buffer, e.BytesRecorded, _capture!.WaveFormat);
        LevelChanged?.Invoke(this, new(left, right));
    }

    private void Capture_RecordingStopped(object? sender, StoppedEventArgs e)
    {
        _writer?.Dispose();
        _writer = null;
        if (_capture is not null)
        {
            _capture.DataAvailable -= Capture_DataAvailable;
            _capture.RecordingStopped -= Capture_RecordingStopped;
            _capture.Dispose();
            _capture = null;
        }
        _paused = false;
        _pauseStartedUtc = null;
        if (_deleteAfterStop && !string.IsNullOrWhiteSpace(_outputPath))
        {
            try { if (File.Exists(_outputPath)) File.Delete(_outputPath); }
            catch (Exception deleteError)
            {
                RecordingStopped?.Invoke(this, new StoppedEventArgs(deleteError));
                return;
            }
            finally { _deleteAfterStop = false; }
        }
        RecordingStopped?.Invoke(this, e);
    }

    private static (float Left, float Right) ReadPeak(byte[] buffer, int count, WaveFormat format)
    {
        float left = 0, right = 0;
        var channels = Math.Max(1, format.Channels);
        if (format.Encoding == WaveFormatEncoding.IeeeFloat && format.BitsPerSample == 32)
        {
            var samples = count / 4;
            for (var i = 0; i < samples; i++)
            {
                var value = Math.Abs(BitConverter.ToSingle(buffer, i * 4));
                if (i % channels == 0) left = Math.Max(left, value); else right = Math.Max(right, value);
            }
        }
        else if (format.BitsPerSample == 16)
        {
            var samples = count / 2;
            for (var i = 0; i < samples; i++)
            {
                var value = Math.Abs(BitConverter.ToInt16(buffer, i * 2) / 32768f);
                if (i % channels == 0) left = Math.Max(left, value); else right = Math.Max(right, value);
            }
        }
        if (channels == 1) right = left;
        return (Math.Clamp(left, 0, 1), Math.Clamp(right, 0, 1));
    }

    public void Dispose()
    {
        try { Stop(); } catch { }
        _writer?.Dispose();
        _capture?.Dispose();
    }
}