using NAudio.Wave;

namespace PilotEars.Audio;

// A mixer input that ducks itself in response to an external trigger envelope.
// Used to bring Discord audio (captured via WASAPI loopback on Discord's own device) into PilotEars's
// output, so the mix is fully under our control — no per-app Windows volume
// games, no USB-speakerphone DSPs ignoring us. Reliable.
public sealed class DuckedSecondaryInput : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly Func<float> _triggerProvider;

    public WaveFormat WaveFormat => _source.WaveFormat;

    public bool Enabled { get; set; } = true;
    public float MixLevel { get; set; } = 1.0f;          // 0..1 master gain when not ducked
    public float DuckAmount { get; set; } = 0.94f;       // 0..1 reduction at full duck
    public float TriggerThreshold { get; set; } = 0.02f; // linear envelope (~ -34 dB)
    public float AttackMs { get; set; } = 30f;
    public float ReleaseMs { get; set; } = 400f;
    public float HysteresisDb { get; set; } = 6f;
    public int MinHoldMs { get; set; } = 250;

    private float _currentDuck;
    private bool _wasDucking;
    private DateTime _holdUntilUtc = DateTime.MinValue;

    // For diagnostics: 0 = not ducked, 1 = fully ducked
    public float CurrentDuck => _currentDuck;

    public DuckedSecondaryInput(ISampleProvider source, Func<float> triggerProvider)
    {
        _source = source;
        _triggerProvider = triggerProvider;
    }

    public int Read(float[] buffer, int offset, int count)
    {
        var read = _source.Read(buffer, offset, count);
        if (read == 0) return 0;

        // Decide should-duck once per buffer (cheap; envelope is slow-moving)
        var level = _triggerProvider();
        bool shouldDuck;
        if (!Enabled)
        {
            shouldDuck = false;
        }
        else if (_wasDucking)
        {
            var offThresh = TriggerThreshold * MathF.Pow(10f, -HysteresisDb / 20f);
            shouldDuck = DateTime.UtcNow < _holdUntilUtc || level > offThresh;
        }
        else
        {
            shouldDuck = level > TriggerThreshold;
            if (shouldDuck) _holdUntilUtc = DateTime.UtcNow.AddMilliseconds(MinHoldMs);
        }
        _wasDucking = shouldDuck;

        float target = shouldDuck ? 1f : 0f;
        int sr = WaveFormat.SampleRate;
        int ch = WaveFormat.Channels;
        float attackCoef = 1f - MathF.Exp(-1f / (sr * AttackMs / 1000f));
        float releaseCoef = 1f - MathF.Exp(-1f / (sr * ReleaseMs / 1000f));

        int frames = read / ch;
        var mix = MixLevel;
        var duckAmt = DuckAmount;

        for (int f = 0; f < frames; f++)
        {
            float coef = target > _currentDuck ? attackCoef : releaseCoef;
            _currentDuck += (target - _currentDuck) * coef;

            float gain = mix * (1f - _currentDuck * duckAmt);
            int idx = offset + f * ch;
            for (int c = 0; c < ch; c++)
                buffer[idx + c] *= gain;
        }

        return read;
    }
}
