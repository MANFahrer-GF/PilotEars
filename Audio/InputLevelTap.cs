using NAudio.Wave;

namespace PilotEars.Audio;

// Pass-through provider that tracks two level signals:
//  - CurrentPeak: instantaneous peak per buffer (drives the UI meter — responsive)
//  - CurrentEnvelope: smoothed envelope (drives the duck trigger — stable across
//    syllable gaps, doesn't flutter on word-internal silences)
public sealed class InputLevelTap : ISampleProvider
{
    private readonly ISampleProvider _source;
    private volatile float _currentPeak;
    private volatile float _currentEnvelope;

    private float _env;
    private readonly float _releaseCoef;

    public WaveFormat WaveFormat => _source.WaveFormat;
    public float CurrentPeak => _currentPeak;
    public float CurrentEnvelope => _currentEnvelope;

    public InputLevelTap(ISampleProvider source)
    {
        _source = source;
        // ~150 ms envelope release — long enough to bridge syllable gaps in speech,
        // short enough to drop noticeably once the transmission ends.
        var releaseMs = 150f;
        _releaseCoef = MathF.Exp(-1f / (source.WaveFormat.SampleRate * releaseMs / 1000f));
    }

    public int Read(float[] buffer, int offset, int count)
    {
        var read = _source.Read(buffer, offset, count);
        float peak = 0f;
        float env = _env;
        for (int i = 0; i < read; i++)
        {
            var a = MathF.Abs(buffer[offset + i]);
            if (a > peak) peak = a;
            // attack: instant rise; release: exponential decay
            if (a > env) env = a;
            else env *= _releaseCoef;
        }
        _env = env;
        _currentPeak = peak;
        _currentEnvelope = env;
        return read;
    }
}
