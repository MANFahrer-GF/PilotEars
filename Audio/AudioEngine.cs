using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace PilotEars.Audio;

public sealed class AudioEngine : IDisposable
{
    // ── vPilot capture chain ─────────────────────────────────────────────
    private WasapiCapture? _capture;
    private BufferedWaveProvider? _captureBuffer;
    private InputLevelTap? _inputTap;
    private Normalizer? _normalizer;
    private Panner? _panner;
    private BrickWallLimiter? _limiter;
    private InputLevelTap? _outputTap;

    // ── Output ───────────────────────────────────────────────────────────
    private WasapiOut? _output;

    public bool IsRunning { get; private set; }
    public float CurrentInputPeak => _inputTap?.CurrentPeak ?? 0f;
    public float CurrentInputEnvelope => _inputTap?.CurrentEnvelope ?? 0f;
    public float CurrentOutputPeak => _outputTap?.CurrentPeak ?? 0f;
    public float CurrentAgcGainDb => _normalizer?.CurrentGainDb ?? 0f;
    public float CurrentLimiterReductionDb => _limiter?.CurrentReductionDb ?? 0f;

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

    public void Start(MMDevice inputDevice, MMDevice outputDevice, int latencyMs)
    {
        Stop();

        // ── vPilot capture (loopback from the unused output device) ──
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
        _outputTap = new InputLevelTap(_limiter);

        _output = new WasapiOut(outputDevice, AudioClientShareMode.Shared, useEventSync: true, latency: latencyMs);
        _output.Init(_outputTap);

        _capture.StartRecording();
        _output.Play();
        IsRunning = true;
    }

    public void Stop()
    {
        if (!IsRunning && _capture is null && _output is null) return;
        try { _output?.Stop(); } catch { }
        try { _capture?.StopRecording(); } catch { }
        _output?.Dispose();
        _capture?.Dispose();
        _output = null;
        _capture = null;
        _captureBuffer = null;
        _inputTap = null;
        _outputTap = null;
        _normalizer = null;
        _panner = null;
        _limiter = null;
        IsRunning = false;
    }

    public void Dispose() => Stop();
}
