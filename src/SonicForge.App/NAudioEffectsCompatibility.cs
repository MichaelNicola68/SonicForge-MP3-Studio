using NAudio.Dsp;
using NAudio.Wave;

namespace NAudio.Effects;

public sealed class EqualizerBand
{
    public float Frequency { get; init; }
    public float Bandwidth { get; init; } = 1.0f;
    public float GainDb { get; set; }

    public static EqualizerBand Peaking(float frequency, float bandwidth, float gainDb) =>
        new() { Frequency = frequency, Bandwidth = bandwidth, GainDb = gainDb };
}

public sealed class Equalizer
{
    private BiQuadFilter[][] _filters = Array.Empty<BiQuadFilter[]>();
    private int _sampleRate;
    private int _channels;

    public Equalizer(IEnumerable<EqualizerBand> bands) => Bands = bands.ToList();

    public List<EqualizerBand> Bands { get; }
    public bool Bypass { get; set; }

    public void Configure(WaveFormat format)
    {
        _sampleRate = format.SampleRate;
        _channels = format.Channels;
        RebuildFilters();
    }

    public void Update()
    {
        if (_sampleRate > 0 && _channels > 0) RebuildFilters();
    }

    internal void Transform(float[] buffer, int offset, int count)
    {
        if (Bypass || _filters.Length == 0) return;
        for (var n = 0; n < count; n++)
        {
            var channel = n % _channels;
            var sample = buffer[offset + n];
            foreach (var filter in _filters[channel]) sample = filter.Transform(sample);
            buffer[offset + n] = Math.Clamp(sample, -1f, 1f);
        }
    }

    private void RebuildFilters()
    {
        _filters = new BiQuadFilter[_channels][];
        for (var channel = 0; channel < _channels; channel++)
            _filters[channel] = Bands.Select(b => BiQuadFilter.PeakingEQ(_sampleRate, b.Frequency, b.Bandwidth, b.GainDb)).ToArray();
    }
}

public sealed class EffectSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly Equalizer _effect;

    public EffectSampleProvider(ISampleProvider source, Equalizer effect)
    {
        _source = source;
        _effect = effect;
        _effect.Configure(source.WaveFormat);
    }

    public WaveFormat WaveFormat => _source.WaveFormat;

    public int Read(float[] buffer, int offset, int count)
    {
        var read = _source.Read(buffer, offset, count);
        _effect.Transform(buffer, offset, read);
        return read;
    }
}
