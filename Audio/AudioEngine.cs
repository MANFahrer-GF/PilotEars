using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace PilotEars.Audio;

public sealed class AudioEngine : IDisposable
{
    // ── vPilot capture chain (primary) ──────────────────────────────────
    private WasapiCapture? _capture;
    private BufferedWaveProvider? _captureBuffer;
    private InputLevelTap? _inputTap;
    private Normalizer? _normalizer;
    private Panner? _panner;
    private BrickWallLimiter? _limiter;
    private InputLevelTap? _outputTap;

    // ── Optional Discord capture chain (mixed in) ───────────────────────
    private WasapiCapture? _discordCapture;
    private BufferedWaveProvider? _discordBuffer;
    private DuckedSecondaryInput? _discordMix;

    // ── Output ──────────────────────────────────────────────────────────
    private WasapiOut? _output;

    public bool IsRunning { get; private set; }
    public float CurrentInputPeak => _inputTap?.CurrentPeak ?? 0f;
    public float CurrentInputEnvelope => _inputTap?.CurrentEnvelope ?? 0f;
    public float CurrentOutputPeak => _outputTap?.CurrentPeak ?? 0f;
    public float CurrentAgcGainDb => _normalizer?.CurrentGainDb ?? 0f;
    public float CurrentLimiterReductionDb => _limiter?.CurrentReductionDb ?? 0f;

    public bool HasDiscordMix => _discordMix is not null;
    public float CurrentDiscordMixDuck => _discordMix?.CurrentDuck ?? 0f;

    public bool Bypass
    {
        set
        {
            if (_normalizer is not null) _normalizer.Bypass = value;
            if (_limiter is not null) _limiter.Bypass = value;
        }
    }

    public float Pan
    {
        get => _panner?.Pan ?? 0f;
        set { if (_panner is not null) _panner.Pan = value; }
    }

    public float NormalizerTargetDb
    {
        set { if (_normalizer is not null) _normalizer.TargetDb = value; }
    }

    public float LimiterCeilingDb { set { _limiter?.SetCeilingDb(value); } }
    public float LimiterReleaseMs { set { if (_limiter is not null) _limiter.ReleaseMs = value; } }
    public float LimiterLookaheadMs { set { _limiter?.SetLookaheadMs(value); } }

    // Discord-mix runtime controls
    public bool DiscordMixEnabled { set { if (_discordMix is not null) _discordMix.Enabled = value; } }
    public float DiscordMixLevel { set { if (_discordMix is not null) _discordMix.MixLevel = value; } }
    public float DiscordMixDuckAmount { set { if (_discordMix is not null) _discordMix.DuckAmount = value; } }
    public float DiscordMixTriggerThreshold { set { if (_discordMix is not null) _discordMix.TriggerThreshold = value; } }

    public void Start(MMDevice inputDevice, MMDevice outputDevice, int latencyMs,
                      MMDevice? discordSource = null)
    {
        Stop();

        // ── Primary capture (vPilot via loopback) ──
        _capture = new WasapiLoopbackCapture(inputDevice);
        var srcFormat = _capture.WaveFormat;
        _captureBuffer = new BufferedWaveProvider(srcFormat)
        {
            BufferDuration = TimeSpan.FromMilliseconds(Math.Max(latencyMs * 4, 200)),
            DiscardOnBufferOverflow = true,
        };
        _capture.DataAvailable += (_, e) =>
        {
            if (e.BytesRecorded > 0)
                _captureBuffer.AddSamples(e.Buffer, 0, e.BytesRecorded);
        };

        ISampleProvider primary = _captureBuffer.ToSampleProvider();
        _inputTap = new InputLevelTap(primary);
        _normalizer = new Normalizer(_inputTap, targetDb: -18f);
        _panner = new Panner(_normalizer, pan: 0f);
        _limiter = new BrickWallLimiter(_panner, ceilingDb: -1f, lookaheadMs: 5f);
        // panner forces stereo at source SR; this is our "target" mix format
        var mixFormat = _limiter.WaveFormat;

        ISampleProvider outChain = _limiter;

        // ── Optional Discord secondary capture, mixed in ──
        if (discordSource is not null)
        {
            _discordCapture = new WasapiLoopbackCapture(discordSource);
            var dFormat = _discordCapture.WaveFormat;
            _discordBuffer = new BufferedWaveProvider(dFormat)
            {
                BufferDuration = TimeSpan.FromMilliseconds(Math.Max(latencyMs * 4, 200)),
                DiscardOnBufferOverflow = true,
            };
            _discordCapture.DataAvailable += (_, e) =>
            {
                if (e.BytesRecorded > 0)
                    _discordBuffer.AddSamples(e.Buffer, 0, e.BytesRecorded);
            };

            ISampleProvider discordChain = _discordBuffer.ToSampleProvider();
            // make sure format matches mixFormat (sample rate + channels)
            discordChain = MatchFormat(discordChain, mixFormat);
            _discordMix = new DuckedSecondaryInput(discordChain, () => _inputTap.CurrentEnvelope);

            var mixer = new MixingSampleProvider(mixFormat);
            mixer.AddMixerInput(_limiter);
            mixer.AddMixerInput(_discordMix);
            _outputTap = new InputLevelTap(mixer);
        }
        else
        {
            _outputTap = new InputLevelTap(_limiter);
        }

        _output = new WasapiOut(outputDevice, AudioClientShareMode.Shared, useEventSync: true, latency: latencyMs);
        _output.Init(_outputTap);

        _capture.StartRecording();
        _discordCapture?.StartRecording();
        _output.Play();
        IsRunning = true;
    }

    private static ISampleProvider MatchFormat(ISampleProvider source, WaveFormat target)
    {
        var src = source.WaveFormat;
        if (src.SampleRate == target.SampleRate && src.Channels == target.Channels)
            return source;

        // sample-rate match first
        if (src.SampleRate != target.SampleRate)
            source = new WdlResamplingSampleProvider(source, target.SampleRate);

        // channel-count match
        if (source.WaveFormat.Channels != target.Channels)
        {
            if (target.Channels == 2 && source.WaveFormat.Channels == 1)
                source = new MonoToStereoSampleProvider(source);
            else if (target.Channels == 1 && source.WaveFormat.Channels == 2)
                source = new StereoToMonoSampleProvider(source);
        }
        return source;
    }

    public void Stop()
    {
        if (!IsRunning && _capture is null && _output is null && _discordCapture is null) return;
        try { _output?.Stop(); } catch { }
        try { _capture?.StopRecording(); } catch { }
        try { _discordCapture?.StopRecording(); } catch { }
        _output?.Dispose();
        _capture?.Dispose();
        _discordCapture?.Dispose();
        _output = null;
        _capture = null;
        _discordCapture = null;
        _captureBuffer = null;
        _discordBuffer = null;
        _inputTap = null;
        _outputTap = null;
        _normalizer = null;
        _panner = null;
        _limiter = null;
        _discordMix = null;
        IsRunning = false;
    }

    public void Dispose() => Stop();
}
