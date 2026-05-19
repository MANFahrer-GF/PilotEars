using NAudio.Wave;

namespace PilotEars.Audio;

// Simple AGC / loudness leveler. Tracks RMS via 1-pole envelope,
// computes makeup gain to push RMS toward TargetDb. Smoothed by
// separate attack (gain coming down) and release (gain going up).
// Includes a gate: below GateDb input, gain is frozen (avoids
// boosting silence/noise floor between transmissions).
public sealed class Normalizer : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly int _channels;
    private readonly int _sampleRate;

    private float _rmsEnvSq;     // squared-RMS envelope
    private float _currentGain = 1f;
    public float CurrentGainDb => 20f * MathF.Log10(MathF.Max(_currentGain, 1e-6f));
    public bool Bypass { get; set; }
    private float _rmsCoef;
    private float _attackCoef;
    private float _releaseCoef;

    // user-controlled (thread-safe via volatile reads)
    private volatile float _targetLinear;
    private volatile float _maxGainLinear;
    private volatile float _gateLinear;

    public WaveFormat WaveFormat => _source.WaveFormat;

    public float TargetDb
    {
        set => _targetLinear = DbToLin(value);
    }

    public float MaxGainDb
    {
        set => _maxGainLinear = DbToLin(value);
    }

    public float GateDb
    {
        set => _gateLinear = DbToLin(value);
    }

    public Normalizer(ISampleProvider source,
                      float targetDb = -18f,
                      float maxGainDb = 24f,
                      float gateDb = -55f,
                      float attackMs = 80f,
                      float releaseMs = 600f,
                      float rmsWindowMs = 50f)
    {
        _source = source;
        _channels = source.WaveFormat.Channels;
        _sampleRate = source.WaveFormat.SampleRate;
        TargetDb = targetDb;
        MaxGainDb = maxGainDb;
        GateDb = gateDb;
        _attackCoef = TimeToCoef(attackMs);
        _releaseCoef = TimeToCoef(releaseMs);
        _rmsCoef = TimeToCoef(rmsWindowMs);
    }

    public int Read(float[] buffer, int offset, int count)
    {
        int read = _source.Read(buffer, offset, count);
        if (Bypass) return read;
        int frames = read / _channels;

        float target = _targetLinear;
        float maxGain = _maxGainLinear;
        float gate = _gateLinear;
        float gateSq = gate * gate;

        for (int f = 0; f < frames; f++)
        {
            // mean-square across channels for this frame
            float sumSq = 0f;
            int idx = offset + f * _channels;
            for (int c = 0; c < _channels; c++)
            {
                float s = buffer[idx + c];
                sumSq += s * s;
            }
            float meanSq = sumSq / _channels;

            // 1-pole RMS envelope on squared signal
            _rmsEnvSq += (meanSq - _rmsEnvSq) * _rmsCoef;

            // desired gain to bring RMS to target (only if signal above gate)
            float desiredGain;
            if (_rmsEnvSq < gateSq)
            {
                desiredGain = _currentGain; // freeze — no boost during silence
            }
            else
            {
                float rms = MathF.Sqrt(_rmsEnvSq);
                desiredGain = target / rms;
                if (desiredGain > maxGain) desiredGain = maxGain;
                if (desiredGain < 0.01f) desiredGain = 0.01f;
            }

            // smooth: faster when reducing gain (attack), slower when raising (release)
            float coef = desiredGain < _currentGain ? _attackCoef : _releaseCoef;
            _currentGain += (desiredGain - _currentGain) * coef;

            for (int c = 0; c < _channels; c++)
                buffer[idx + c] *= _currentGain;
        }

        return read;
    }

    private float TimeToCoef(float ms)
    {
        if (ms <= 0f) return 1f;
        // standard 1-pole time-constant: y[n] = y[n-1] + c*(x - y[n-1])
        return 1f - MathF.Exp(-1f / (_sampleRate * ms / 1000f));
    }

    private static float DbToLin(float db) => MathF.Pow(10f, db / 20f);
}
