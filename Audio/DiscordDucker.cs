using NAudio.CoreAudioApi;
using System.Diagnostics;

namespace PilotEars.Audio;

// Monitors a trigger signal (PilotEars input peak) and ducks Discord's
// audio session volume when that signal exceeds a threshold.
// Saves Discord's original volume and restores it on stop / when not ducking.
public sealed class DiscordDucker : IDisposable
{
    private readonly Func<float> _triggerPeakProvider;
    private CancellationTokenSource? _cts;
    private Task? _loopTask;

    // user-controlled
    public bool Enabled { get; set; } = true;
    public float TriggerThreshold { get; set; } = 0.02f;  // linear envelope (~ -34 dB) — turn-ON
    public float DuckAmount { get; set; } = 0.5f;          // 0..1 → 0..MaxReductionDb dB cut
    public float MaxReductionDb { get; set; } = 40f;       // what 100% on the slider really means
    public int AttackMs { get; set; } = 30;
    public int ReleaseMs { get; set; } = 400;
    public int PollMs { get; set; } = 20;

    // Hysteresis: turn-OFF threshold is this many dB below TriggerThreshold.
    // Prevents flutter at the boundary — once we duck, we stay ducked until the
    // signal drops clearly. 6 dB is a good speech-friendly value.
    public float HysteresisDb { get; set; } = 6f;

    // Minimum time the duck stays engaged once triggered. Bridges natural
    // pauses between words and syllables so the ducker doesn't pump.
    public int MinHoldMs { get; set; } = 250;

    // Process names to duck (case-insensitive substring match).
    //   "Discord"  also matches DiscordPTB, DiscordCanary, DiscordDevelopment
    //   "Vesktop"  / "ArmCord" / "WebCord" / "Dorion" = popular Discord clients/forks
    public string[] ProcessNameMatches { get; set; } = new[]
    {
        "Discord", "Vesktop", "ArmCord", "WebCord", "Dorion"
    };

    // User-supplied additional name fragments (case-insensitive substring),
    // for when the app shows a name the built-in list doesn't catch.
    public string[] ExtraMatches { get; set; } = Array.Empty<string>();

    // ALSO lower the master volume of the audio device Discord is playing on.
    // Use when per-app volume doesn't take effect — common for USB conference
    // speakers (Anker PowerConf, Jabra, …) that have built-in DSPs and ignore
    // per-session volume control. Side effect: any other app on the same
    // device also gets quieter while the duck is active.
    public bool DuckDeviceMasterAlso { get; set; } = false;

    // All process names that had an audio session in the last scan — for diagnostics
    // ("why doesn't Discord get found?" → show user what we DID see).
    public string LastSeenProcessNames { get; private set; } = "";

    // What the discord session(s) volume was when we first saw them (the value
    // we'll restore on Stop) vs what we last actually wrote. Lets the user
    // visually verify we ARE changing Discord's per-app Windows volume.
    public float? LastDiscordOriginalVolume { get; private set; }
    public float? LastDiscordAppliedVolume { get; private set; }

    // The render endpoint(s) on which Discord audio sessions were last found.
    // Lets us tell the user "Discord plays on X" — if X is not the device they
    // listen on, ducking will be invisible to them.
    public string LastDiscordDeviceNames { get; private set; } = "";

    // Peak audio level (linear 0..1) of Discord sessions, max across all found.
    // 0 means Discord isn't producing audio right now (even if session exists).
    // Lets the UI show a Discord-level bar so user can see if Discord is alive.
    public float LastDiscordPeak { get; private set; }

    // ID of the device Discord is currently playing on — used by the fast peak
    // reader so the UI bar can update every frame without re-enumerating
    // all audio sessions (which is expensive and only happens on full scan).
    // Public set so the UI can prime it directly when the user picks a Discord
    // source from the dropdown — the meter then works immediately without a scan.
    public string? LastDiscordDeviceId { get; set; }

    private static readonly MMDeviceEnumerator _peakEnum = new();

    // FAST peak read for the live meter — opens the cached device and reads
    // its built-in peak meter. Sub-millisecond, safe to call every UI tick.
    // Returns 0 if no Discord device known yet.
    public float ReadDiscordPeakFast()
    {
        var id = LastDiscordDeviceId;
        if (string.IsNullOrEmpty(id)) return 0f;
        try
        {
            using var device = _peakEnum.GetDevice(id);
            return device.AudioMeterInformation.MasterPeakValue;
        }
        catch { return 0f; }
    }

    public DiscordDucker(Func<float> triggerPeakProvider)
    {
        _triggerPeakProvider = triggerPeakProvider;
    }

    private DateTime _forceUntilUtc = DateTime.MinValue;
    private bool _inForceTest;
    public bool IsCurrentlyDucking { get; private set; }

    // How many Discord audio sessions we saw in the last poll, across all
    // active render endpoints. 0 means Discord isn't producing audio that
    // we can find — usually because Discord isn't open, or no audio is
    // playing in it (open a voice channel or run the mic test).
    public int LastDiscordSessionCount { get; private set; }

    public bool IsRunning => _loopTask is not null;

    // 0.0 = no reduction, 1.0 = fully at user's DuckAmount. Smoothly tracks
    // the live ducking state so the UI can visualize it in real time.
    public float CurrentDuckAmount { get; private set; }

    // Force the duck to be active for `ms` milliseconds. Returns the max
    // number of Discord audio sessions seen during the test — caller can use
    // 0 to mean "Discord not found / not playing audio".
    public async Task<int> ForceDuckAsync(int ms)
    {
        bool startedForTest = _loopTask is null;
        if (startedForTest) Start();
        _forceUntilUtc = DateTime.UtcNow.AddMilliseconds(ms);

        // Poll the session-count during the force window so we capture
        // the peak, even if Discord's session appears momentarily.
        int maxFound = 0;
        int waited = 0;
        const int step = 100;
        while (waited < ms)
        {
            await Task.Delay(Math.Min(step, ms - waited));
            if (LastDiscordSessionCount > maxFound) maxFound = LastDiscordSessionCount;
            waited += step;
        }

        if (startedForTest)
        {
            await Task.Delay(Math.Max(ReleaseMs, 200));
            Stop();
        }
        return maxFound;
    }

    public void Start()
    {
        Stop();
        _cts = new CancellationTokenSource();
        _loopTask = Task.Run(() => Loop(_cts.Token));
    }

    // One-shot detection: enumerates audio sessions once with duck=0 (no
    // volume changes) so the UI can find Discord even when the ducker loop
    // isn't running (e.g. before the user clicks Start). Updates the
    // LastDiscordDeviceNames / LastDiscordSessionCount / LastSeenProcessNames
    // properties — the Auto button reads from these.
    public void ScanOnce()
    {
        try
        {
            var enumerator = new MMDeviceEnumerator();
            ApplyDuckToDiscord(enumerator, 0f);
        }
        catch { /* enumeration is best-effort */ }
    }

    // Discord's audio session can be transiently inactive at the moment of a
    // single scan (e.g. between sounds, during init). Retry a few times with
    // short delays so the user clicking "Auto" doesn't get a false negative.
    // Returns true as soon as ANY scan in the window found a Discord session.
    public async Task<bool> ScanWithRetryAsync(int attempts = 4, int delayMs = 150)
    {
        for (int i = 0; i < attempts; i++)
        {
            ScanOnce();
            if (LastDiscordSessionCount > 0) return true;
            if (i < attempts - 1) await Task.Delay(delayMs);
        }
        return false;
    }

    public void Stop()
    {
        try { _cts?.Cancel(); } catch { }
        try { _loopTask?.Wait(500); } catch { }
        _cts?.Dispose();
        _cts = null;
        _loopTask = null;
        RestoreAllDiscordVolumes();
    }

    public void Dispose() => Stop();

    private readonly Dictionary<uint, float> _originalVolumes = new();
    private readonly Dictionary<string, float> _originalDeviceVolumes = new();

    private void Loop(CancellationToken ct)
    {
        float currentDuck = 0f;          // 0 = no duck, 1 = full duck
        bool wasDucking = false;          // schmitt state
        DateTime holdUntilUtc = DateTime.MinValue;
        var enumerator = new MMDeviceEnumerator();

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var level = _triggerPeakProvider();
                bool forced = DateTime.UtcNow < _forceUntilUtc;
                _inForceTest = forced;

                bool shouldDuck;
                if (forced)
                {
                    shouldDuck = true;
                }
                else if (!Enabled)
                {
                    shouldDuck = false;
                }
                else if (wasDucking)
                {
                    // currently ducking: hold for MinHoldMs after trigger, then
                    // require signal to drop to off-threshold before releasing
                    var offThresh = TriggerThreshold * MathF.Pow(10f, -HysteresisDb / 20f);
                    shouldDuck = DateTime.UtcNow < holdUntilUtc || level > offThresh;
                }
                else
                {
                    // currently quiet: trigger only when crossing the main threshold
                    shouldDuck = level > TriggerThreshold;
                    if (shouldDuck) holdUntilUtc = DateTime.UtcNow.AddMilliseconds(MinHoldMs);
                }

                wasDucking = shouldDuck;
                IsCurrentlyDucking = currentDuck > 0.05f;

                // smooth volume transition toward target
                float target = shouldDuck ? 1f : 0f;
                float coef = target > currentDuck
                    ? PollMs / (float)Math.Max(1, AttackMs)
                    : PollMs / (float)Math.Max(1, ReleaseMs);
                coef = Math.Clamp(coef, 0f, 1f);
                currentDuck += (target - currentDuck) * coef;
                CurrentDuckAmount = currentDuck;

                ApplyDuckToDiscord(enumerator, currentDuck);
            }
            catch { /* swallow per-tick races, retry next poll */ }

            try { Task.Delay(PollMs, ct).Wait(ct); } catch { }
        }

        RestoreAllDiscordVolumes();
        CurrentDuckAmount = 0f;
    }

    private void ApplyDuckToDiscord(MMDeviceEnumerator enumerator, float duck)
    {
        int found = 0;
        var seen = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var discordDevices = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        string? discordDevId = null;
        float maxPeak = 0f;
        try
        {
            // Discord can be playing on ANY render endpoint. Some endpoints
            // (disconnected HDMI, sleeping Bluetooth, …) throw when their
            // AudioSessionManager is queried, so each device gets its own
            // try/catch — one bad endpoint must not abort the whole scan.
            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                try
                {
                    var sessions = device.AudioSessionManager.Sessions;
                    if (sessions is null) continue;

                    for (int i = 0; i < sessions.Count; i++)
                    {
                        try
                        {
                            var session = sessions[i];
                            uint pid;
                            try { pid = session.GetProcessID; } catch { continue; }
                            if (pid == 0) continue;

                            // Robust name lookup: ProcessName often fails for Discord
                            // (Electron with restricted sub-processes), so we fall back
                            // to parsing the SessionInstanceIdentifier path or the
                            // session DisplayName. Diagnostics always show whatever we found.
                            var appName = GetSessionAppName(session, pid);
                            if (appName is not null) seen.Add(appName);

                            if (!MatchesDuckList(appName)) continue;

                            found++;
                            try { discordDevices.Add(device.FriendlyName); } catch { }
                            try { discordDevId ??= device.ID; } catch { }
                            // Read the audio meter — tells us if Discord is making sound RIGHT NOW
                            try
                            {
                                var p = session.AudioMeterInformation.MasterPeakValue;
                                if (p > maxPeak) maxPeak = p;
                            }
                            catch { }

                            // Optional: lower entire device's master volume + mute when fully ducked.
                            // Workaround for USB conf speakers (Anker, Jabra) that ignore per-app
                            // volume because of internal DSP. Mute is a separate API call that some
                            // such devices still respect even when volume changes don't reach them.
                            if (DuckDeviceMasterAlso)
                            {
                                try
                                {
                                    var endpointVol = device.AudioEndpointVolume;
                                    var devId = device.ID;
                                    if (!_originalDeviceVolumes.ContainsKey(devId))
                                        _originalDeviceVolumes[devId] = endpointVol.MasterVolumeLevelScalar;

                                    var origDevVol = _originalDeviceVolumes[devId];
                                    var newDevVol = origDevVol * (1f - duck * DuckAmount);
                                    if (newDevVol < 0f) newDevVol = 0f;
                                    if (newDevVol > 1f) newDevVol = 1f;
                                    if (Math.Abs(endpointVol.MasterVolumeLevelScalar - newDevVol) > 0.005f)
                                        endpointVol.MasterVolumeLevelScalar = newDevVol;

                                    // Mute when ducking is hard — separate API, hardware may respect
                                    // this even when volume slider is ignored.
                                    bool shouldMute = duck * DuckAmount > 0.5f;
                                    if (endpointVol.Mute != shouldMute)
                                        endpointVol.Mute = shouldMute;
                                }
                                catch { }
                            }

                            var sav = session.SimpleAudioVolume;

                            // Refresh "original" when idle so user can manually
                            // adjust Discord's volume in the Windows mixer and
                            // we won't overwrite it on the next poll.
                            bool isIdle = duck < 0.005f;
                            if (isIdle || !_originalVolumes.ContainsKey(pid))
                                _originalVolumes[pid] = sav.Volume;

                            var original = _originalVolumes[pid];

                            if (_inForceTest && duck > 0.3f)
                            {
                                // TEST MODE: hard mute = definitive, instantly visible
                                // in the Windows volume mixer. Volume stays at original
                                // so user sees clear "muted" indicator return when test ends.
                                try { if (!sav.Mute) sav.Mute = true; } catch { }
                            }
                            else
                            {
                                // NORMAL MODE: linear volume reduction (the original
                                // approach — user reported this works; dB-based was
                                // technically nicer but regressed for them).
                                // Always clear Mute so we never leave a session stuck.
                                try { if (sav.Mute) sav.Mute = false; } catch { }

                                var newVol = original * (1f - duck * DuckAmount);
                                if (newVol < 0f) newVol = 0f;
                                if (newVol > 1f) newVol = 1f;

                                if (Math.Abs(sav.Volume - newVol) > 0.005f)
                                    sav.Volume = newVol;
                            }

                            LastDiscordOriginalVolume = original;
                            LastDiscordAppliedVolume = sav.Mute ? 0f : sav.Volume;
                        }
                        catch { /* session went away mid-iteration — skip */ }
                    }
                }
                catch { /* this device unreachable — skip it, keep scanning others */ }
                finally
                {
                    try { device.Dispose(); } catch { }
                }
            }
        }
        finally
        {
            LastDiscordSessionCount = found;
            LastSeenProcessNames = string.Join(", ", seen);
            LastDiscordDeviceNames = string.Join(", ", discordDevices);
            // Only update LastDiscordDeviceId when we ACTUALLY found Discord.
            // A transient scan with no Discord audio (e.g. between sounds) would
            // otherwise null the user's manually-selected device id from the
            // dropdown — and the live meter would die until they re-pick.
            if (found > 0) LastDiscordDeviceId = discordDevId;
            LastDiscordPeak = maxPeak;
        }
    }

    private static string? SafeProcessName(uint pid)
    {
        try { using var p = Process.GetProcessById((int)pid); return p.ProcessName; }
        catch { return null; }
    }

    // Best-effort name for an audio session. Tries:
    //   1. Process.ProcessName via PID — usually works for normal apps
    //   2. Parse exe filename out of the session-instance-identifier path
    //      (works even when Process.GetProcessById is denied — typical for
    //      Discord's Electron sub-processes)
    //   3. Session DisplayName as last resort
    private static string? GetSessionAppName(NAudio.CoreAudioApi.AudioSessionControl session, uint pid)
    {
        var procName = SafeProcessName(pid);
        if (!string.IsNullOrEmpty(procName)) return procName;

        try
        {
            // Format example:
            //   "{0.0.0.00000000}.{f9b...}|\\Device\\HarddiskVolume3\\Users\\me\\AppData\\Local\\Discord\\app-1.0.9163\\Discord.exe%b{...}"
            var ident = session.GetSessionInstanceIdentifier;
            if (!string.IsNullOrEmpty(ident))
            {
                var pipeIdx = ident.IndexOf('|');
                if (pipeIdx >= 0)
                {
                    var path = ident.Substring(pipeIdx + 1);
                    var pctIdx = path.IndexOf('%');
                    if (pctIdx > 0) path = path.Substring(0, pctIdx);
                    var fileName = System.IO.Path.GetFileNameWithoutExtension(path);
                    if (!string.IsNullOrEmpty(fileName)) return fileName;
                }
            }
        }
        catch { }

        try
        {
            var dn = session.DisplayName;
            if (!string.IsNullOrEmpty(dn)) return dn;
        }
        catch { }

        return null;
    }

    private bool MatchesDuckList(string? appName)
    {
        if (string.IsNullOrEmpty(appName)) return false;
        foreach (var m in ProcessNameMatches)
            if (!string.IsNullOrEmpty(m) && appName.Contains(m, StringComparison.OrdinalIgnoreCase))
                return true;
        foreach (var m in ExtraMatches)
            if (!string.IsNullOrEmpty(m) && appName.Contains(m, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    private void RestoreAllDiscordVolumes()
    {
        if (_originalVolumes.Count == 0 && _originalDeviceVolumes.Count == 0) return;
        try
        {
            var enumerator = new MMDeviceEnumerator();
            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                try
                {
                    // restore device master volume + unmute if we touched it
                    try
                    {
                        if (_originalDeviceVolumes.TryGetValue(device.ID, out var origDev))
                        {
                            var ep = device.AudioEndpointVolume;
                            if (ep.Mute) ep.Mute = false;
                            ep.MasterVolumeLevelScalar = origDev;
                        }
                    }
                    catch { }

                    var sessions = device.AudioSessionManager.Sessions;
                    if (sessions is null) continue;
                    for (int i = 0; i < sessions.Count; i++)
                    {
                        try
                        {
                            var session = sessions[i];
                            uint pid;
                            try { pid = session.GetProcessID; } catch { continue; }
                            if (_originalVolumes.TryGetValue(pid, out var orig))
                            {
                                try
                                {
                                    var sav = session.SimpleAudioVolume;
                                    if (sav.Mute) sav.Mute = false;
                                    sav.Volume = orig;
                                }
                                catch { }
                            }
                        }
                        catch { }
                    }
                }
                catch { }
                finally { try { device.Dispose(); } catch { } }
            }
        }
        catch { }
        _originalVolumes.Clear();
        _originalDeviceVolumes.Clear();
        LastDiscordSessionCount = 0;
        LastDiscordOriginalVolume = null;
        LastDiscordAppliedVolume = null;
        LastDiscordDeviceNames = "";
        LastDiscordDeviceId = null;
        LastDiscordPeak = 0f;
    }
}
