using NAudio.Wave;

namespace PilotEars.Audio;

// Constant-power pan. Sums input to mono, then distributes to stereo out.
// Pan range: -1 (full left) ... 0 (center) ... +1 (full right).
// Output is always stereo (forces stereo WaveFormat).
public sealed class Panner : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly int _srcChannels;
    private float[] _srcBuffer = Array.Empty<float>();

    private volatile float _pan;
    private float _gainL = 0.7071f;
    private float _gainR = 0.7071f;

    public WaveFormat WaveFormat { get; }

    public float Pan
    {
        get => _pan;
        set
        {
            var p = Math.Clamp(value, -1f, 1f);
            _pan = p;
            // constant-power: angle 0..pi/2 maps from full-left to full-right
            var angle = (p + 1f) * (float)(Math.PI / 4.0);
            _gainL = (float)Math.Cos(angle);
            _gainR = (float)Math.Sin(angle);
        }
    }

    public Panner(ISampleProvider source, float pan = 0f)
    {
        _source = source;
        _srcChannels = source.WaveFormat.Channels;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 2);
        Pan = pan;
    }

    public int Read(float[] buffer, int offset, int count)
    {
        // count is samples requested in output (stereo); we need count/2 frames,
        // which means count/2 * srcChannels source samples.
        var frames = count / 2;
        var needed = frames * _srcChannels;
        if (_srcBuffer.Length < needed)
            _srcBuffer = new float[needed];

        var read = _source.Read(_srcBuffer, 0, needed);
        var framesRead = read / _srcChannels;

        var gL = _gainL;
        var gR = _gainR;

        for (int f = 0; f < framesRead; f++)
        {
            // sum-to-mono
            float mono = 0f;
            int si = f * _srcChannels;
            for (int c = 0; c < _srcChannels; c++)
                mono += _srcBuffer[si + c];
            if (_srcChannels > 1) mono /= _srcChannels;

            buffer[offset + f * 2]     = mono * gL;
            buffer[offset + f * 2 + 1] = mono * gR;
        }

        return framesRead * 2;
    }
}
