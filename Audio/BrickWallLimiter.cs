using NAudio.Wave;

namespace PilotEars.Audio;

// Brick-wall limiter with optional look-ahead.
// look-ahead = 0  → zero added latency, tiny overshoots possible on transients
// look-ahead > 0  → delays signal by N ms; gain ramps DOWN before peak arrives
//                    → no overshoot, no clicks, costs N ms latency
public sealed class BrickWallLimiter : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly int _channels;
    private readonly int _sampleRate;

    private float _envelope = 1f;
    private float _releaseCoef;
    private float _releaseMs = 50f;
    private float _ceilingLin;

    // look-ahead state
    private int _lookaheadSamples;
    private float[] _delayBuf = Array.Empty<float>();
    private float[] _gainBuf = Array.Empty<float>();
    private int _writeIdx;
    private int _bufLenFrames;

    public WaveFormat WaveFormat => _source.WaveFormat;
    public float CurrentReductionDb => 20f * MathF.Log10(MathF.Max(_envelope, 1e-6f));
    public bool Bypass { get; set; }

    public float ReleaseMs
    {
        get => _releaseMs;
        set { _releaseMs = value; RecalcRelease(); }
    }

    public void SetCeilingDb(float db) => _ceilingLin = DbToLin(db);

    public void SetLookaheadMs(float ms)
    {
        int newSamples = Math.Max(0, (int)(_sampleRate * ms / 1000f));
        if (newSamples == _lookaheadSamples) return;

        // Build new buffers off to the side, then swap references atomically.
        // Audio thread takes local snapshots in Read() — if it's mid-iteration
        // during this swap, it continues safely on the old arrays.
        int newBufLen = Math.Max(1, newSamples + 1);
        var newDelay = new float[newBufLen * _channels];
        var newGain = new float[newBufLen];
        for (int i = 0; i < newGain.Length; i++) newGain[i] = 1f;

        _lookaheadSamples = newSamples;
        _bufLenFrames = newBufLen;
        _delayBuf = newDelay;
        _gainBuf = newGain;
        _writeIdx = 0;
        _envelope = 1f;
    }

    public BrickWallLimiter(ISampleProvider source, float ceilingDb = -1f, float lookaheadMs = 5f)
    {
        _source = source;
        _channels = source.WaveFormat.Channels;
        _sampleRate = source.WaveFormat.SampleRate;
        _ceilingLin = DbToLin(ceilingDb);
        RecalcRelease();
        SetLookaheadMs(lookaheadMs);
    }

    private void RecalcRelease()
    {
        _releaseCoef = MathF.Exp(-1f / (_sampleRate * _releaseMs / 1000f));
    }

    public int Read(float[] buffer, int offset, int count)
    {
        int read = _source.Read(buffer, offset, count);
        if (Bypass) return read;
        int frames = read / _channels;
        float ceiling = _ceilingLin;

        // Snapshot mutable state ONCE. If SetLookaheadMs swaps these refs mid-read,
        // we keep using the old (still-valid) arrays — no IndexOutOfRange races.
        var delayBuf = _delayBuf;
        var gainBuf = _gainBuf;
        var bufLen = _bufLenFrames;
        var lookahead = _lookaheadSamples;
        var writeIdx = _writeIdx;
        var env = _envelope;
        var releaseCoef = _releaseCoef;

        if (lookahead == 0)
        {
            // fast path: no look-ahead, instant attack
            for (int f = 0; f < frames; f++)
            {
                int idx = offset + f * _channels;
                float peak = 0f;
                for (int c = 0; c < _channels; c++)
                {
                    float a = MathF.Abs(buffer[idx + c]);
                    if (a > peak) peak = a;
                }
                float target = peak > ceiling ? ceiling / peak : 1f;
                if (target < env) env = target;
                else env = 1f - (1f - env) * releaseCoef;

                for (int c = 0; c < _channels; c++)
                {
                    float s = buffer[idx + c] * env;
                    if (s > ceiling) s = ceiling; else if (s < -ceiling) s = -ceiling;
                    buffer[idx + c] = s;
                }
            }
            _envelope = env;
            return read;
        }

        // look-ahead path: write incoming sample to ring, read out delayed sample
        for (int f = 0; f < frames; f++)
        {
            int srcIdx = offset + f * _channels;

            float peak = 0f;
            for (int c = 0; c < _channels; c++)
            {
                float a = MathF.Abs(buffer[srcIdx + c]);
                if (a > peak) peak = a;
            }
            float targetGain = peak > ceiling ? ceiling / peak : 1f;

            int wFrame = writeIdx;
            int wSampIdx = wFrame * _channels;
            for (int c = 0; c < _channels; c++)
                delayBuf[wSampIdx + c] = buffer[srcIdx + c];
            gainBuf[wFrame] = targetGain;

            writeIdx = (wFrame + 1) % bufLen;

            int rFrame = writeIdx; // oldest slot after advancing
            int rSampIdx = rFrame * _channels;

            float upcomingTarget = MathF.Min(gainBuf[wFrame], gainBuf[rFrame]);

            if (upcomingTarget < env) env = upcomingTarget;
            else env = 1f - (1f - env) * releaseCoef;

            for (int c = 0; c < _channels; c++)
            {
                float s = delayBuf[rSampIdx + c] * env;
                if (s > ceiling) s = ceiling; else if (s < -ceiling) s = -ceiling;
                buffer[srcIdx + c] = s;
            }
        }

        _writeIdx = writeIdx;
        _envelope = env;
        return read;
    }

    private static float DbToLin(float db) => MathF.Pow(10f, db / 20f);
}
