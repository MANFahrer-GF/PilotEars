using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using NAudio.CoreAudioApi;
using PilotEars.Audio;
// Disambiguate WPF vs WinForms (we use WPF for everything except NotifyIcon)
using Button = System.Windows.Controls.Button;
using MessageBox = System.Windows.MessageBox;
using Color = System.Windows.Media.Color;
using Brush = System.Windows.Media.Brush;

namespace PilotEars;

public partial class MainWindow : Window
{
    private readonly AudioEngine _engine = new();
    private readonly DiscordDucker _ducker;
    private readonly Settings _settings;
    private readonly DispatcherTimer _meterTimer;
    private readonly DispatcherTimer _vpilotWatchTimer;
    private System.Windows.Forms.NotifyIcon? _trayIcon;
    private bool _wasVPilotRunning;
    private bool _loaded;
    private bool _bypass;
    private string _lang = "EN";

    public MainWindow()
    {
        InitializeComponent();
        _settings = Settings.Load();
        // Ducker triggers off the smoothed envelope (stable across syllable gaps),
        // not the raw peak (would flutter and pump on speech).
        _ducker = new DiscordDucker(() => _engine.CurrentInputEnvelope);

        _meterTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        _meterTimer.Tick += MeterTimer_Tick;
        _meterTimer.Start();

        // Polls every 2 seconds for vPilot/xPilot start — cheap, doesn't impact audio
        _vpilotWatchTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _vpilotWatchTimer.Tick += VPilotWatchTimer_Tick;
        _vpilotWatchTimer.Start();

        Loaded += OnLoaded;
        Closing += OnClosing;
        StateChanged += OnStateChanged;
    }

    private bool _reallyQuit;

    private void OnStateChanged(object? sender, EventArgs e)
    {
        if (!_settings.MinimizeToTray) return;
        if (WindowState == WindowState.Minimized)
        {
            // Tray icon should already be visible (we keep it shown whenever the
            // setting is on). Just hide the window from taskbar.
            EnsureTrayIcon();
            Hide();
        }
    }

    private void EnsureTrayIcon()
    {
        if (_trayIcon is not null) { _trayIcon.Visible = true; return; }

        var icon = RenderLogoToIcon(32);

        _trayIcon = new System.Windows.Forms.NotifyIcon
        {
            Icon = icon,
            Text = "PilotEars",
            Visible = true,
        };
        _trayIcon.DoubleClick += (_, _) => ShowFromTray();

        var menu = new System.Windows.Forms.ContextMenuStrip();
        var showItem = menu.Items.Add(_lang == "DE" ? "Anzeigen" : "Show");
        showItem.Click += (_, _) => ShowFromTray();
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        var quitItem = menu.Items.Add(_lang == "DE" ? "Beenden" : "Quit");
        quitItem.Click += (_, _) =>
        {
            _reallyQuit = true;
            if (_trayIcon is not null) _trayIcon.Visible = false;
            System.Windows.Application.Current.Shutdown();
        };
        _trayIcon.ContextMenuStrip = menu;
    }

    private void HideTrayIcon()
    {
        if (_trayIcon is null) return;
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        _trayIcon = null;
    }

    private System.Drawing.Icon RenderLogoToIcon(int size)
    {
        // The DrawingImage is authored in a 64×64 coordinate system. Scale it
        // down to `size` pixels so the logo fills the target bitmap (without
        // ScaleTransform only the top-left quadrant would render).
        var drawing = ((DrawingImage)FindResource("AppLogo")).Drawing;
        var visual = new DrawingVisual();
        using (var ctx = visual.RenderOpen())
        {
            double scale = size / 64.0;
            ctx.PushTransform(new ScaleTransform(scale, scale));
            ctx.DrawDrawing(drawing);
            ctx.Pop();
        }
        var rtb = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);

        // Convert WPF BitmapSource → PNG bytes → System.Drawing.Bitmap → HICON → Icon
        using var ms = new System.IO.MemoryStream();
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
        encoder.Save(ms);
        ms.Position = 0;
        using var bmp = new System.Drawing.Bitmap(ms);
        return System.Drawing.Icon.FromHandle(bmp.GetHicon());
    }

    private void ShowFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
        if (_trayIcon is not null) _trayIcon.Visible = false;
    }

    // Native title-bar tweaks for Windows 10/11:
    //   1. Strip WS_MAXIMIZEBOX so the disabled max button doesn't render at all
    //      (CanMinimize only greys it out — Win11 still draws a faint outline)
    //   2. Opt the title-bar chrome into dark mode via DwmSetWindowAttribute,
    //      otherwise it stays Windows-default-light against PilotEars's dark UI
    //      and the whole bar looks like a white strip glued onto the app.
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hwnd, int index, int value);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr hwndInsertAfter, int x, int y, int cx, int cy, uint flags);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);
    private const int GWL_STYLE = -16;
    private const int WS_MAXIMIZEBOX = 0x10000;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_FRAMECHANGED = 0x0020;
    // 20 on newer Win10 builds + all Win11; 19 on early Win10 builds. Try 20
    // first, fall back to 19 if the call fails — both are no-ops on older OSes.
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE_LEGACY = 19;

    private void ApplyNativeTitleBarTweaks()
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;

            // 1. Strip maximize button.
            var style = GetWindowLong(hwnd, GWL_STYLE);
            SetWindowLong(hwnd, GWL_STYLE, style & ~WS_MAXIMIZEBOX);

            // 2. Ask DWM to render the title bar in dark mode.
            int useDark = 1;
            int r = DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref useDark, sizeof(int));
            if (r != 0)
                DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE_LEGACY, ref useDark, sizeof(int));

            // 3. Force the chrome to redraw so both changes are visible immediately.
            SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_FRAMECHANGED);
        }
        catch { /* best-effort — non-fatal */ }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        ApplyNativeTitleBarTweaks();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Belt-and-braces: re-apply title-bar tweaks after WPF finishes its own
        // chrome setup. Some Windows 11 builds reset attributes between
        // SourceInitialized and Loaded.
        ApplyNativeTitleBarTweaks();
        SetWindowIconFromLogo();
        ApplySettingsToUi();
        SetLanguage(_settings.Language ?? "EN");
        RebuildPresetButtons();
        _loaded = true;

        // If a background update download already finished before MainWindow
        // loaded, show the banner immediately. Otherwise wait for the event.
        if (App.PendingUpdateInfo is not null) ShowUpdateBanner();
        App.UpdateReady += ShowUpdateBanner;

        // Honour StartMinimized BEFORE device load — minimize ASAP so the
        // window doesn't sit on screen while devices enumerate.
        if (_settings.StartMinimized)
        {
            WindowState = WindowState.Minimized;
            // If tray mode is on, OnStateChanged hides + shows tray icon.
            // Without tray mode, this is a normal taskbar-minimize.
        }

        await LoadDevicesAsync();
    }

    private void SetWindowIconFromLogo()
    {
        // render the DrawingImage logo to a bitmap for taskbar + title bar
        var drawing = ((DrawingImage)FindResource("AppLogo")).Drawing;
        var visual = new DrawingVisual();
        using (var ctx = visual.RenderOpen())
            ctx.DrawDrawing(drawing);
        var rtb = new RenderTargetBitmap(64, 64, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        Icon = rtb;
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        // If tray mode is on and user clicked X (not Quit from tray menu),
        // hide to tray instead of actually closing.
        if (_settings.MinimizeToTray && !_reallyQuit)
        {
            e.Cancel = true;
            EnsureTrayIcon();
            Hide();
            return;
        }

        _meterTimer.Stop();
        _vpilotWatchTimer.Stop();
        if (_trayIcon is not null) { _trayIcon.Visible = false; _trayIcon.Dispose(); }
        PersistSettings();
        _ducker.Dispose();
        _engine.Dispose();
    }

    private void VPilotWatchTimer_Tick(object? sender, EventArgs e)
    {
        if (!_loaded || !_settings.AutoEngageOnVPilot) return;
        bool running;
        try
        {
            // Lenient StartsWith match — catches vPilot, vPilotClient,
            // vPilotInjector, xPilot, xPilotClient and any other variants
            // that the exact-name check would miss.
            running = AnyProcessStartingWith("vPilot") || AnyProcessStartingWith("xPilot");
        }
        catch { return; }

        // Rising edge only: when vPilot starts AND we're not already running.
        // Don't auto-stop when vPilot quits (user can stop manually) — avoids
        // accidentally killing audio if vPilot crashes/restarts.
        if (running && !_wasVPilotRunning && !_engine.IsRunning)
        {
            StartEngine(showErrors: false);
        }
        _wasVPilotRunning = running;
    }

    private static bool AnyProcessStartingWith(string prefix)
    {
        try
        {
            foreach (var p in System.Diagnostics.Process.GetProcesses())
            {
                try
                {
                    if (p.ProcessName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    {
                        p.Dispose();
                        return true;
                    }
                }
                catch { /* access denied for some system processes — skip */ }
                finally { try { p.Dispose(); } catch { } }
            }
        }
        catch { }
        return false;
    }

    private void AutoStartWithWindows_Changed(object sender, RoutedEventArgs e)
    {
        if (!_loaded) return;
        AutoStart.SetEnabled(AutoStartWithWindowsBox.IsChecked == true);
    }

    private void AutoEngageOnVPilot_Changed(object sender, RoutedEventArgs e)
    {
        if (!_loaded) return;
        _settings.AutoEngageOnVPilot = AutoEngageOnVPilotBox.IsChecked == true;
        _settings.Save();
    }

    private void MinimizeToTray_Changed(object sender, RoutedEventArgs e)
    {
        if (!_loaded) return;
        _settings.MinimizeToTray = MinimizeToTrayBox.IsChecked == true;
        _settings.Save();
        if (_settings.MinimizeToTray) EnsureTrayIcon();
        else HideTrayIcon();
    }

    private void StartMinimized_Changed(object sender, RoutedEventArgs e)
    {
        if (!_loaded) return;
        _settings.StartMinimized = StartMinimizedBox.IsChecked == true;
        _settings.Save();
    }

    private void ShowUpdateBanner()
    {
        // Idempotent — fine to call twice (event + check on load).
        UpdateBanner.Content = Strings(_settings.Language ?? "EN")["updateReady"];
        UpdateBanner.Visibility = Visibility.Visible;
    }

    private void UpdateBanner_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            // Persist any in-flight settings before we restart.
            _settings.Save();
            // Velopack: apply the downloaded update on exit and relaunch.
            App.PendingUpdateManager?.WaitExitThenApplyUpdates(App.PendingUpdateInfo);
            System.Windows.Application.Current.Shutdown();
        }
        catch
        {
            // If anything goes sideways, just shut down — Velopack will apply
            // on the next manual launch.
            System.Windows.Application.Current.Shutdown();
        }
    }

    // ============== Meters ==============
    private static readonly SolidColorBrush LedOff = new(Color.FromRgb(0x3a, 0x3d, 0x45));
    private static readonly SolidColorBrush LedOn = new(Color.FromRgb(0xf5, 0x9e, 0x0b));

    private void MeterTimer_Tick(object? sender, EventArgs e)
    {
        // Note: NO periodic ScanOnce here — full COM session enumeration on the
        // UI thread blocks dropdown rendering. LastDiscordDeviceId is set either
        // by the ducker loop (when engine running) or by the Auto button.
        // ReadDiscordPeakFast just reads the cached device's meter — sub-ms.

        // Live ducking LED + visualization bar reflect ducker state regardless
        // of engine state (so user can see during Test, even when stopped).
        // When the user has unchecked "Ducking aktiv", we leave Discord alone
        // completely — meters dim, diagnostic shows "—", no session reads.
        bool monitorDiscord = DuckEnabledBox.IsChecked == true;
        if (monitorDiscord)
        {
            UpdateDuckingLed(_ducker.IsCurrentlyDucking);
            UpdateDuckLiveMeter();
            UpdateDiscordDiagnostics();
            UpdateDiscordPeakMeter();
        }
        else
        {
            UpdateDuckingLed(false);
            DuckLiveMeter.Value = 0;
            DuckLiveLabel.Text = "—";
            DiscordDeviceLabel.Text = $"{Strings(_lang)["diagDiscordDev"]} —";
            DiscordDeviceLabel.Foreground = (Brush)FindResource("TextTertiary");
            DiscordPeakMeter.Value = 0;
            DiscordPeakLabel.Text = "-∞";
            _smoothedDiscordPeak = 0f;
        }

        if (!_engine.IsRunning)
        {
            InputMeter.Value = 0; AgcMeter.Value = 50; OutputMeter.Value = 0;
            InputMeterLabel.Text = "-∞"; AgcMeterLabel.Text = "0"; OutputMeterLabel.Text = "-∞";
            return;
        }
        var inDb = PeakToDb(_engine.CurrentInputPeak);
        var outDb = PeakToDb(_engine.CurrentOutputPeak);
        var agcDb = _engine.CurrentAgcGainDb;

        InputMeter.Value = DbToMeter(inDb);
        OutputMeter.Value = DbToMeter(outDb);
        AgcMeter.Value = Math.Clamp(50.0 + agcDb * (50.0 / 24.0), 0, 100);

        InputMeterLabel.Text = float.IsNegativeInfinity(inDb) ? "-∞" : $"{inDb:0}";
        OutputMeterLabel.Text = float.IsNegativeInfinity(outDb) ? "-∞" : $"{outDb:0}";
        AgcMeterLabel.Text = agcDb >= 0 ? $"+{agcDb:0}" : $"{agcDb:0}";
    }

    private float _smoothedDiscordPeak;
    private void UpdateDiscordPeakMeter()
    {
        // FAST direct read from device-level meter — every tick (33ms = 30fps).
        var raw = _ducker.ReadDiscordPeakFast();
        // VU-meter smoothing: instant attack, slow release (~70ms tau at 30fps).
        // Eliminates "choppy" updates caused by sampling-window aliasing.
        _smoothedDiscordPeak = Math.Max(raw, _smoothedDiscordPeak * 0.65f);
        var db = PeakToDb(_smoothedDiscordPeak);
        DiscordPeakMeter.Value = DbToMeter(db);
        DiscordPeakLabel.Text = float.IsNegativeInfinity(db) ? "-∞" : $"{db:0}";
    }

    private void UpdateDiscordDiagnostics()
    {
        var t = Strings(_lang);
        var discordDevs = _ducker.LastDiscordDeviceNames;
        DiscordDeviceLabel.Text = string.IsNullOrEmpty(discordDevs)
            ? $"{t["diagDiscordDev"]} —"
            : $"{t["diagDiscordDev"]} {discordDevs}";
        DiscordDeviceLabel.Foreground = string.IsNullOrEmpty(discordDevs)
            ? (Brush)FindResource("TextTertiary")
            : (Brush)FindResource("TextSecondary");
    }

    private void UpdateDuckLiveMeter()
    {
        // Linear ducking: discord_vol = original * (1 - duck * userAmount).
        var duck = _ducker.CurrentDuckAmount;
        var reductionPct = duck * _ducker.DuckAmount * 100f;
        DuckLiveMeter.Value = Math.Clamp(reductionPct, 0, 100);
        var remainingPct = 100f - reductionPct;
        DuckLiveLabel.Text = $"{remainingPct:0}%";
    }

    private bool _lastLedState;
    private int _lastSessionCount = -1;
    private bool _lastDuckerRunning;
    private void UpdateDuckingLed(bool ducking)
    {
        var sessionCount = _ducker.LastDiscordSessionCount;
        var ducRunning = _ducker.IsRunning;
        bool changed = ducking != _lastLedState
                    || sessionCount != _lastSessionCount
                    || ducRunning != _lastDuckerRunning;
        if (!changed) return;

        _lastLedState = ducking;
        _lastSessionCount = sessionCount;
        _lastDuckerRunning = ducRunning;

        DuckingLed.Fill = ducking ? LedOn : LedOff;

        var t = Strings(_lang);
        string text;
        if (!ducRunning)
            text = t["ledIdle"];
        else if (sessionCount == 0)
            text = t["ledNoDiscord"];
        else if (ducking)
            text = $"{t["ledActive"]} · {sessionCount}";
        else
            text = $"{t["ledIdle"]} · {sessionCount}";
        DuckingLedLabel.Text = text;

        DuckingLedLabel.Foreground = (ducking || (ducRunning && sessionCount == 0))
            ? (Brush)FindResource("TextPrimary")
            : (Brush)FindResource("TextTertiary");

        // Diagnostic tooltip: show ALL process names with audio sessions,
        // so user can spot the right name when Discord isn't being matched.
        var seen = _ducker.LastSeenProcessNames;
        var tt = string.IsNullOrEmpty(seen)
            ? Strings(_lang)["ttLedEmpty"]
            : Strings(_lang)["ttLedSeen"] + "\n  " + seen.Replace(", ", "\n  ");
        DuckingLedLabel.ToolTip = tt;
        DuckingLed.ToolTip = tt;
    }

    private static float PeakToDb(float linear)
    {
        if (linear <= 1e-6f) return float.NegativeInfinity;
        return 20f * MathF.Log10(linear);
    }

    private static double DbToMeter(float db)
    {
        if (float.IsNegativeInfinity(db)) return 0;
        return Math.Clamp((db + 60.0) * (100.0 / 60.0), 0, 100);
    }

    // ============== Devices / Settings ==============
    private async Task LoadDevicesAsync(bool disableWhileLoading = true)
    {
        // disableWhileLoading=false when called from DropDownOpened — toggling
        // IsEnabled while the popup is open would force WPF to close the popup
        // on the same frame, making the dropdown appear to "not open at all".
        if (disableWhileLoading)
        {
            InputDeviceBox.IsEnabled = false;
            OutputDeviceBox.IsEnabled = false;
        }

        // Enumerate + extract strings on background thread (COM init can be
        // ~500 ms cold). Only primitive strings cross back to the UI thread —
        // MMDevice instances themselves are STA-bound and must NOT cross.
        var (inputs, outputs) = await Task.Run(() =>
        {
            try
            {
                var enumerator = new MMDeviceEnumerator();
                var ins = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
                    .Select(d => new DeviceItem(d.ID, d.FriendlyName)).ToList();
                var outs = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
                    .Select(d => new DeviceItem(d.ID, d.FriendlyName)).ToList();
                return (ins, outs);
            }
            catch
            {
                return (new List<DeviceItem>(), new List<DeviceItem>());
            }
        });

        InputDeviceBox.ItemsSource = inputs;
        OutputDeviceBox.ItemsSource = outputs;

        // Discord source: same render endpoints + a "(none)" sentinel at the top.
        var discordChoices = new List<DeviceItem> { new("", Strings(_lang)["discordSrcNone"]) };
        discordChoices.AddRange(inputs);
        DiscordSourceBox.ItemsSource = discordChoices;

        if (_settings.InputDeviceId is not null)
            InputDeviceBox.SelectedItem = inputs.FirstOrDefault(d => d.Id == _settings.InputDeviceId);
        if (InputDeviceBox.SelectedItem is null && inputs.Count > 0)
            InputDeviceBox.SelectedIndex = 0;

        if (_settings.OutputDeviceId is not null)
            OutputDeviceBox.SelectedItem = outputs.FirstOrDefault(d => d.Id == _settings.OutputDeviceId);
        if (OutputDeviceBox.SelectedItem is null && outputs.Count > 0)
            OutputDeviceBox.SelectedIndex = 0;

        var savedDisc = _settings.DiscordSourceDeviceId;
        DiscordSourceBox.SelectedItem = string.IsNullOrEmpty(savedDisc)
            ? discordChoices[0]
            : discordChoices.FirstOrDefault(d => d.Id == savedDisc) ?? discordChoices[0];

        if (disableWhileLoading)
        {
            InputDeviceBox.IsEnabled = true;
            OutputDeviceBox.IsEnabled = true;
        }
    }

    private static MMDevice? ResolveDevice(string id)
    {
        try
        {
            // COM is warm at this point — sub-10 ms lookup on UI thread.
            return new MMDeviceEnumerator().GetDevice(id);
        }
        catch { return null; }
    }

    private void ApplySettingsToUi()
    {
        LatencySlider.Value = _settings.LatencyMs;
        TargetSlider.Value = _settings.NormalizerTargetDb;
        CeilingSlider.Value = _settings.LimiterCeilingDb;
        ReleaseSlider.Value = _settings.LimiterReleaseMs;
        LookaheadSlider.Value = _settings.LimiterLookaheadMs;
        PanSlider.Value = _settings.Pan;
        DuckEnabledBox.IsChecked = _settings.DuckEnabled;
        DuckAmountSlider.Value = _settings.DuckAmount;
        DuckThresholdSlider.Value = 20.0 * Math.Log10(Math.Max(_settings.DuckThreshold, 1e-6f));
        // DuckDeviceMasterAlso is decided per-tick in ApplyAllToDucker() based on
        // whether Discord plays on the same device as PilotEars output.
        // Auto-start checkboxes
        AutoStartWithWindowsBox.IsChecked = AutoStart.IsEnabled;
        AutoEngageOnVPilotBox.IsChecked = _settings.AutoEngageOnVPilot;
        MinimizeToTrayBox.IsChecked = _settings.MinimizeToTray;
        StartMinimizedBox.IsChecked = _settings.StartMinimized;
        if (_settings.MinimizeToTray) EnsureTrayIcon();
        ApplyAllToDucker();
        UpdateDiscordControlsEnabled();
        UpdateLabels();
    }

    private void PersistSettings()
    {
        if (InputDeviceBox.SelectedItem is DeviceItem inItem) _settings.InputDeviceId = inItem.Id;
        if (OutputDeviceBox.SelectedItem is DeviceItem outItem) _settings.OutputDeviceId = outItem.Id;
        _settings.LatencyMs = (int)LatencySlider.Value;
        _settings.NormalizerTargetDb = (float)TargetSlider.Value;
        _settings.LimiterCeilingDb = (float)CeilingSlider.Value;
        _settings.LimiterReleaseMs = (float)ReleaseSlider.Value;
        _settings.LimiterLookaheadMs = (float)LookaheadSlider.Value;
        _settings.Pan = (float)PanSlider.Value;
        _settings.DuckEnabled = DuckEnabledBox.IsChecked == true;
        _settings.DuckAmount = (float)DuckAmountSlider.Value;
        _settings.DuckThreshold = (float)Math.Pow(10.0, DuckThresholdSlider.Value / 20.0);
        if (DiscordSourceBox.SelectedItem is DeviceItem dItem)
            _settings.DiscordSourceDeviceId = string.IsNullOrEmpty(dItem.Id) ? null : dItem.Id;
        _settings.Language = _lang;
        _settings.Save();
    }

    private void UpdateLabels()
    {
        if (LatencyLabel is null || TargetLabel is null || CeilingLabel is null ||
            ReleaseLabel is null || LookaheadLabel is null || PanLabel is null ||
            DuckAmountLabel is null || DuckThresholdLabel is null)
            return;
        LatencyLabel.Text = $"{(int)LatencySlider.Value}";
        TargetLabel.Text = $"{TargetSlider.Value:0.#}";
        LookaheadLabel.Text = $"{(int)LookaheadSlider.Value}";
        CeilingLabel.Text = $"{CeilingSlider.Value:0.#}";
        ReleaseLabel.Text = $"{(int)ReleaseSlider.Value}";
        var p = PanSlider.Value;
        var t = Strings(_lang);
        PanLabel.Text = Math.Abs(p) < 0.05 ? t["panCenter"] : (p < 0 ? $"L{(int)(-p * 100)}" : $"R{(int)(p * 100)}");
        // Linear: 50% = halves volume, 100% = mute. Crank toward 100% for strong ducking.
        var duckPct = (int)(DuckAmountSlider.Value * 100);
        DuckAmountLabel.Text = $"{duckPct}%";
        DuckAmountSlider.ToolTip = $"{duckPct}% volume reduction (0 = no duck, 100 = mute)";
        DuckThresholdLabel.Text = $"{(int)DuckThresholdSlider.Value}";
    }

    // ============== Start / Stop / Bypass ==============
    private void StartStopButton_Click(object sender, RoutedEventArgs e)
    {
        if (_engine.IsRunning) StopEngine();
        else StartEngine(showErrors: true);
    }

    private void StopEngine()
    {
        var t = Strings(_lang);
        _ducker.Stop();
        _engine.Stop();
        StartStopButton.Content = t["btnStart"];
        StartStopButton.ToolTip = t["ttStart"];
        RouteInfoLabel.Text = t["stoppedHint"];
        BypassButton.IsEnabled = false;
    }

    private bool StartEngine(bool showErrors)
    {
        var t = Strings(_lang);
        if (InputDeviceBox.SelectedItem is not DeviceItem inItem ||
            OutputDeviceBox.SelectedItem is not DeviceItem outItem)
        {
            if (showErrors)
                MessageBox.Show(this, t["pickDevicesMsg"], "PilotEars",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        var inDev = ResolveDevice(inItem.Id);
        var outDev = ResolveDevice(outItem.Id);
        if (inDev is null || outDev is null)
        {
            if (showErrors)
                MessageBox.Show(this, t["audioStartErr"] + "\n\nDevice no longer available.",
                    "PilotEars", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
        try
        {
            _engine.Start(inDev, outDev, (int)LatencySlider.Value);
            ApplyAllToEngine();
            ApplyAllToDucker();
            _engine.Bypass = _bypass;
            if (DuckEnabledBox.IsChecked == true)
                _ducker.Start();
            StartStopButton.Content = t["btnStop"];
            StartStopButton.ToolTip = t["ttStop"];
            BypassButton.IsEnabled = true;
            var totalLatency = (int)LatencySlider.Value + (int)LookaheadSlider.Value;
            RouteInfoLabel.Text =
                $"{t["runCapturing"]}: {Truncate(inItem.FriendlyName, 30)}\n" +
                $"{t["runPlaying"]}: {Truncate(outItem.FriendlyName, 30)}\n" +
                $"{t["runLatency"]} ≈ {totalLatency} ms";
            PersistSettings();
            return true;
        }
        catch (Exception ex)
        {
            if (showErrors)
                MessageBox.Show(this, $"{t["audioStartErr"]}\n\n{ex.Message}", "PilotEars",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            _engine.Stop();
            return false;
        }
    }

    private void ApplyAllToEngine()
    {
        _engine.NormalizerTargetDb = (float)TargetSlider.Value;
        _engine.LimiterCeilingDb = (float)CeilingSlider.Value;
        _engine.LimiterReleaseMs = (float)ReleaseSlider.Value;
        _engine.LimiterLookaheadMs = (float)LookaheadSlider.Value;
        _engine.Pan = (float)PanSlider.Value;
    }

    private void ApplyAllToDucker()
    {
        _ducker.Enabled = DuckEnabledBox.IsChecked == true;
        _ducker.DuckAmount = (float)DuckAmountSlider.Value;
        _ducker.TriggerThreshold = (float)Math.Pow(10.0, DuckThresholdSlider.Value / 20.0);
        _ducker.AttackMs = Math.Max(1, _settings.DuckAttackMs);
        _ducker.ReleaseMs = Math.Max(1, _settings.DuckReleaseMs);

        // Auto-pick duck mechanic by comparing Discord's device with PilotEars
        // output. If they match, per-app SimpleAudioVolume is enough and we must
        // NOT touch device-master (would also quiet the radio). If they differ,
        // Discord is on a separate physical device (e.g. Anker speakerphone) and
        // we duck the master + mute that device — per-app on its own often gets
        // ignored by USB DSP speakers.
        string? discordDevId = null;
        if (DiscordSourceBox.SelectedItem is DeviceItem dItem && !string.IsNullOrEmpty(dItem.Id))
            discordDevId = dItem.Id;

        string? outputDevId = null;
        if (OutputDeviceBox.SelectedItem is DeviceItem oItem && !string.IsNullOrEmpty(oItem.Id))
            outputDevId = oItem.Id;

        bool separateDevice = !string.IsNullOrEmpty(discordDevId)
                              && !string.IsNullOrEmpty(outputDevId)
                              && !string.Equals(discordDevId, outputDevId, StringComparison.OrdinalIgnoreCase);
        _ducker.DuckDeviceMasterAlso = separateDevice;
    }

    private void BypassButton_Click(object sender, RoutedEventArgs e)
    {
        _bypass = !_bypass;
        _engine.Bypass = _bypass;
        var t = Strings(_lang);
        BypassButton.Content = _bypass ? t["btnProcess"] : t["btnBypass"];
        BypassButton.ToolTip = _bypass ? t["ttProcess"] : t["ttBypass"];
        BypassButton.Background = _bypass
            ? new SolidColorBrush(Color.FromRgb(0xf9, 0x73, 0x16))
            : new SolidColorBrush(Color.FromRgb(0x2a, 0x2d, 0x35));
    }

    // ============== Slider event handlers ==============
    // Manual slider tweak clears the "active preset" highlight (since values
    // no longer match a clean preset). Programmatic ApplyPreset sets the flag
    // so its slider writes don't immediately clear the highlight it's about to set.
    private void ClearPresetIfManual()
    {
        if (_loaded && !_applyingPreset && _activePreset is not null)
            SetActivePreset(null);
    }

    private void LatencySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => UpdateLabels();
    private void TargetSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        UpdateLabels();
        if (_loaded) _engine.NormalizerTargetDb = (float)TargetSlider.Value;
        ClearPresetIfManual();
    }
    private void CeilingSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        UpdateLabels();
        if (_loaded) _engine.LimiterCeilingDb = (float)CeilingSlider.Value;
        ClearPresetIfManual();
    }
    private void LookaheadSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        UpdateLabels();
        if (_loaded) _engine.LimiterLookaheadMs = (float)LookaheadSlider.Value;
        ClearPresetIfManual();
    }
    private void ReleaseSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        UpdateLabels();
        if (_loaded) _engine.LimiterReleaseMs = (float)ReleaseSlider.Value;
        ClearPresetIfManual();
    }
    private void PanSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        UpdateLabels();
        if (_loaded) _engine.Pan = (float)PanSlider.Value;
    }
    private void DuckEnabled_Changed(object sender, RoutedEventArgs e)
    {
        if (!_loaded) return;
        _ducker.Enabled = DuckEnabledBox.IsChecked == true;
        if (_engine.IsRunning)
        {
            if (_ducker.Enabled) _ducker.Start(); else _ducker.Stop();
        }
        UpdateDiscordControlsEnabled();
        _settings.DuckEnabled = _ducker.Enabled;
        _settings.Save();
    }

    // Grey out (or re-enable) Discord-section controls when "Ducking aktiv" is
    // toggled. Off = PilotEars touches nothing on Discord, no scans, no meters.
    private void UpdateDiscordControlsEnabled()
    {
        bool on = DuckEnabledBox.IsChecked == true;
        DiscordSourceBox.IsEnabled = on;
        DiscordAutoBtn.IsEnabled = on;
        DuckAmountSlider.IsEnabled = on;
        DuckThresholdSlider.IsEnabled = on;
        TestDuckButton.IsEnabled = on;
    }
    private void DuckAmountSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        UpdateLabels();
        if (_loaded) _ducker.DuckAmount = (float)DuckAmountSlider.Value;
    }
    private void DuckThresholdSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        UpdateLabels();
        if (!_loaded) return;
        _ducker.TriggerThreshold = (float)Math.Pow(10.0, DuckThresholdSlider.Value / 20.0);
    }

    // ============== Presets ==============
    private bool _applyingPreset;
    private string? _activePreset;
    // True when the currently active preset is one of the user's custom slots,
    // false when it's a builtin. Needed to keep highlight unambiguous when a
    // custom preset has the same name as a builtin (e.g. user saved "VATSIM").
    private bool _activePresetIsCustom;

    private void ApplyPreset(float target, float ceiling, float release, float lookahead,
                             float pan = float.NaN, float latency = float.NaN)
    {
        _applyingPreset = true;
        try
        {
            TargetSlider.Value = target;
            CeilingSlider.Value = ceiling;
            ReleaseSlider.Value = release;
            LookaheadSlider.Value = lookahead;
            if (!float.IsNaN(pan)) PanSlider.Value = pan;
            if (!float.IsNaN(latency)) LatencySlider.Value = latency;
        }
        finally { _applyingPreset = false; }
    }

    private void SetActivePreset(string? name, bool isCustom = false)
    {
        _activePreset = name;
        _activePresetIsCustom = isCustom;
        UpdatePresetHighlight();
    }

    private void UpdatePresetHighlight()
    {
        if (PresetBtnVatsim is null) return;
        var normal = (Style)FindResource("PresetButton");
        var active = (Style)FindResource("PresetButtonActive");

        // Builtins only light up when the active preset is a builtin — never
        // when a custom preset happens to share the same name.
        bool builtinActive = !_activePresetIsCustom;
        PresetBtnVatsim.Style     = builtinActive && _activePreset == "VATSIM"     ? active : normal;
        PresetBtnLive.Style       = builtinActive && _activePreset == "Live"       ? active : normal;
        PresetBtnAggressive.Style = builtinActive && _activePreset == "Aggressive" ? active : normal;
        PresetBtnMinimal.Style    = builtinActive && _activePreset == "Minimal"    ? active : normal;

        // Custom row: highlight by name, but only when a custom preset is active.
        foreach (var child in PresetsPanel.Children)
        {
            if (child is Button btn && btn.Content is string s)
                btn.Style = _activePresetIsCustom && s == _activePreset ? active : normal;
        }
    }

    // VATSIM = reference "Katie tool" values: target -18, ceiling -3, release 200,
    // look-ahead 150, latency 170. User confirmed this is what works for them.
    private void Preset_VATSIM(object sender, RoutedEventArgs e)      { ApplyPreset(-18f, -3f, 200f, 150f, latency: 170f); SetActivePreset("VATSIM"); }
    // Live = low-latency (was the old "VATSIM" — for users who want minimal delay)
    private void Preset_Live(object sender, RoutedEventArgs e)        { ApplyPreset(-15f, -3f, 50f, 5f);    SetActivePreset("Live"); }
    private void Preset_Aggressive(object sender, RoutedEventArgs e)  { ApplyPreset(-12f, -3f, 30f, 5f);    SetActivePreset("Aggressive"); }
    private void Preset_Minimal(object sender, RoutedEventArgs e)     { ApplyPreset(-20f, -1f, 100f, 0f);   SetActivePreset("Minimal"); }

    private void RebuildPresetButtons()
    {
        PresetsPanel.Children.Clear();
        var presetStyle = (Style)FindResource("PresetButton");

        foreach (var p in _settings.CustomPresets)
        {
            var btn = new Button { Style = presetStyle, Content = p.Name };
            var captured = p;
            btn.Click += (_, _) =>
            {
                ApplyPreset(captured.TargetDb, captured.CeilingDb, captured.ReleaseMs, captured.LookaheadMs, captured.Pan);
                SetActivePreset(captured.Name, isCustom: true);
            };

            var menu = new System.Windows.Controls.ContextMenu();
            var del = new System.Windows.Controls.MenuItem { Header = Strings(_lang)["btnDeletePreset"] };
            del.Click += (_, _) => { _settings.CustomPresets.Remove(captured); _settings.Save(); RebuildPresetButtons(); };
            menu.Items.Add(del);
            btn.ContextMenu = menu;

            PresetsPanel.Children.Add(btn);
        }

        // Subtle "+" button at end (ghost style, not screaming for attention)
        var add = new Button
        {
            Style = presetStyle,
            Content = "+",
            FontSize = 14,
            FontWeight = FontWeights.Bold,
            Padding = new Thickness(10, 4, 10, 4),
            ToolTip = Strings(_lang)["dlgSaveTitle"],
        };
        add.Click += SavePreset_Click;
        PresetsPanel.Children.Add(add);
    }

    private void SavePreset_Click(object sender, RoutedEventArgs e)
    {
        var t = Strings(_lang);
        var dlg = new PresetNameDialog(t["dlgSaveTitle"], t["dlgSavePrompt"],
                                       t["btnSavePreset"], t["btnCancel"]) { Owner = this };
        if (dlg.ShowDialog() != true) return;

        var name = dlg.PresetName;
        // overwrite if name already exists
        _settings.CustomPresets.RemoveAll(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        _settings.CustomPresets.Add(new PresetData
        {
            Name = name,
            TargetDb = (float)TargetSlider.Value,
            CeilingDb = (float)CeilingSlider.Value,
            ReleaseMs = (float)ReleaseSlider.Value,
            LookaheadMs = (float)LookaheadSlider.Value,
            Pan = (float)PanSlider.Value,
        });
        _settings.Save();
        RebuildPresetButtons();
    }

    // ============== Language ==============
    private void LangButton_Click(object sender, RoutedEventArgs e)
    {
        var btn = (ToggleButton)sender;
        var lang = btn == LangDeButton ? "DE" : "EN";
        SetLanguage(lang);
    }

    private void SetLanguage(string lang)
    {
        _lang = lang;
        LangDeButton.IsChecked = lang == "DE";
        LangEnButton.IsChecked = lang == "EN";
        var t = Strings(lang);

        TaglineLabel.Text = t["tagline"];
        PresetLabel.Text = t["preset"];
        CustomPresetLabel.Text = t["customPresets"];
        SectionDevices.Text = t["sectionDevices"];
        SectionNormalizer.Text = t["sectionNormalizer"];
        SectionLimiter.Text = t["sectionLimiter"];
        SectionPan.Text = t["sectionPan"];
        SectionDiscord.Text = t["sectionDiscord"];
        SectionLevels.Text = t["sectionLevels"];

        LabelSource.Text = t["labelSource"];
        LabelOutput.Text = t["labelOutput"];
        LabelDiscordSrc.Text = t["labelDiscordSrc"];
        LabelLatency.Text = t["labelLatency"];
        LabelTarget.Text = t["labelTarget"];
        LabelCeiling.Text = t["labelCeiling"];
        LabelRelease.Text = t["labelRelease"];
        LabelLookahead.Text = t["labelLookahead"];
        LabelDuckAmount.Text = t["labelDuckAmount"];
        LabelDuckThreshold.Text = t["labelDuckThreshold"];
        LabelMeterInput.Text = t["meterInput"];
        LabelMeterAgc.Text = t["meterAgc"];
        LabelMeterOutput.Text = t["meterOutput"];
        LabelMeterDiscord.Text = t["meterDiscord"];
        LabelAutoStart.Text = t["labelAutoStart"];
        LabelAutoEngage.Text = t["labelAutoEngage"];
        LabelMinTray.Text = t["labelMinTray"];
        LabelStartMin.Text = t["labelStartMin"];

        DuckEnabledBox.Content = t["enableDucking"];
        VersionLabel.Text = t["version"];
        if (UpdateBanner.Visibility == Visibility.Visible)
            UpdateBanner.Content = t["updateReady"];

        StartStopButton.Content = _engine.IsRunning ? t["btnStop"] : t["btnStart"];
        BypassButton.Content = _bypass ? t["btnProcess"] : t["btnBypass"];
        StartStopButton.ToolTip = _engine.IsRunning ? t["ttStop"] : t["ttStart"];
        BypassButton.ToolTip = _bypass ? t["ttProcess"] : t["ttBypass"];
        LatencySlider.ToolTip = t["ttLatency"];
        TestDuckButton.Content = t["btnTest"];
        TestDuckButton.ToolTip = t["ttTest"];
        DuckingLedLabel.Text = _lastLedState ? t["ledActive"] : t["ledIdle"];
        LabelDuckLive.Text = t["labelDuckLive"];

        if (!_engine.IsRunning) RouteInfoLabel.Text = t["stoppedHint"];

        FooterPre.Text = t["footerPre"];
        FooterPost.Text = t["footerPost"];

        // Invalidate LED state cache so UpdateDuckingLed re-renders the label
        // with the new language (it otherwise early-returns on unchanged state).
        _lastSessionCount = -1;

        UpdateLabels();
        if (_loaded) RebuildPresetButtons();
    }

    private static Dictionary<string, string> Strings(string lang) => lang == "DE" ? _de : _en;

    private static readonly Dictionary<string, string> _en = new()
    {
        ["version"]            = "v1.6.7 · VATSIM voice polish",
        ["updateReady"]        = "Update ready — click to restart",
        ["tagline"]            = "Real-time audio polishing for VATSIM radio. Evens out quiet and loud pilots, prevents peaks, and ducks Discord automatically.",
        ["preset"]             = "Preset:",
        ["customPresets"]      = "My presets:",
        ["sectionDevices"]     = "AUDIO DEVICES",
        ["sectionNormalizer"]  = "NORMALIZER (AUTO LEVEL)",
        ["sectionLimiter"]     = "BRICK-WALL LIMITER",
        ["sectionPan"]         = "PAN (L ← CENTER → R)",
        ["sectionDiscord"]     = "DISCORD AUTO-DUCKING",
        ["sectionLevels"]      = "LIVE LEVELS",
        ["labelSource"]        = "Source (unused output device vPilot routes to)",
        ["labelOutput"]        = "Output (your headset)",
        ["labelLatency"]       = "Latency (ms)",
        ["labelTarget"]        = "Target loudness (dB)",
        ["labelCeiling"]       = "Ceiling (dB)",
        ["labelRelease"]       = "Release (ms)",
        ["labelLookahead"]     = "Look-ahead (ms) — 0 = zero added latency",
        ["labelDuckAmount"]    = "Duck amount (% reduction)",
        ["labelDuckThreshold"] = "Trigger threshold (dB)",
        ["meterInput"]         = "Input",
        ["meterAgc"]           = "AGC gain",
        ["meterOutput"]        = "Output",
        ["meterDiscord"]       = "Discord",
        ["enableDucking"]      = "Enable ducking",
        ["btnStart"]           = "Start",
        ["btnStop"]            = "Stop",
        ["btnBypass"]          = "Bypass",
        ["btnProcess"]         = "Process",
        ["panCenter"]          = "C",
        ["stoppedHint"]        = "Stopped. Pick a non-monitored source (an output that's not connected to your speakers).",
        ["runCapturing"]       = "Capturing",
        ["runPlaying"]         = "Playing to",
        ["runLatency"]         = "Latency",
        ["pickDevicesMsg"]     = "Pick an input and output device first.",
        ["audioStartErr"]      = "Could not start audio:",
        ["btnSavePreset"]      = "Save",
        ["btnDeletePreset"]    = "Delete",
        ["btnCancel"]          = "Cancel",
        ["dlgSaveTitle"]       = "Save preset",
        ["dlgSavePrompt"]      = "Preset name:",
        ["ttStart"]            = "Begin audio capture + processing",
        ["ttStop"]             = "Stop audio capture",
        ["ttBypass"]           = "Skip normalizer + limiter to hear raw audio (A/B compare)",
        ["ttProcess"]          = "Re-enable normalizer + limiter",
        ["ttLatency"]          = "Buffer size. Lower = less delay, higher = more stable. Takes effect after Stop+Start.",
        ["ledActive"]          = "ducking",
        ["ledIdle"]            = "idle",
        ["ledNoDiscord"]       = "Discord not found",
        ["btnTest"]            = "Test ducking",
        ["testRunning"]        = "Ducking…",
        ["testOk"]             = "✓ Found",
        ["testFailed"]         = "✗ No Discord audio",
        ["ttTest"]             = "Clicks: lowers Discord's volume for 2 seconds, then restores it. Open Discord and play any audio (e.g. mic test) first — then click here and listen / watch Discord's volume slider drop and come back.",
        ["labelDuckLive"]      = "Discord",
        ["ttLedEmpty"]         = "No app audio sessions visible. Open something that plays sound (Discord, browser, …) to see what process names appear here.",
        ["ttLedSeen"]          = "Audio sessions currently visible (we duck anything whose name contains 'Discord', 'Vesktop', 'ArmCord', 'WebCord' or 'Dorion'):",
        ["diagDiscordDev"]     = "Discord plays on:",
        ["labelDiscordSrc"]    = "Discord plays on (optional — used only to duck Discord when radio is active)",
        ["discordSrcNone"]     = "(none — use Windows per-app ducker)",
        ["labelAutoStart"]     = "Start PilotEars with Windows",
        ["labelAutoEngage"]    = "Start engine automatically when vPilot/xPilot runs",
        ["labelMinTray"]       = "Minimize to system tray",
        ["labelStartMin"]      = "Start minimized",
        ["footerPre"]          = "Developed with ",
        ["footerPost"]         = " in Gifhorn  ·  Thomas Kant",
    };

    private static readonly Dictionary<string, string> _de = new()
    {
        ["version"]            = "v1.6.7 · VATSIM-Funkpolitur",
        ["updateReady"]        = "Update bereit — klicken zum Neustart",
        ["tagline"]            = "Echtzeit-Audio-Polishing für VATSIM-Funk. Gleicht laute und leise Piloten an, verhindert Peaks und duckt Discord automatisch.",
        ["preset"]             = "Voreinstellung:",
        ["customPresets"]      = "Eigene:",
        ["sectionDevices"]     = "AUDIO-GERÄTE",
        ["sectionNormalizer"]  = "NORMALISIERUNG (AUTO-PEGEL)",
        ["sectionLimiter"]     = "BRICK-WALL-LIMITER",
        ["sectionPan"]         = "PANORAMA (L ← MITTE → R)",
        ["sectionDiscord"]     = "DISCORD AUTO-DUCKING",
        ["sectionLevels"]      = "LIVE-PEGEL",
        ["labelSource"]        = "Quelle (ungenutzter Ausgang, auf den vPilot routet)",
        ["labelOutput"]        = "Ausgabe (dein Headset)",
        ["labelLatency"]       = "Latenz (ms)",
        ["labelTarget"]        = "Ziel-Lautstärke (dB)",
        ["labelCeiling"]       = "Maximalpegel (dB)",
        ["labelRelease"]       = "Release (ms)",
        ["labelLookahead"]     = "Look-ahead (ms) — 0 = keine Zusatzlatenz",
        ["labelDuckAmount"]    = "Duck-Stärke (% Absenkung)",
        ["labelDuckThreshold"] = "Auslöseschwelle (dB)",
        ["meterInput"]         = "Eingang",
        ["meterAgc"]           = "AGC-Gain",
        ["meterOutput"]        = "Ausgang",
        ["meterDiscord"]       = "Discord",
        ["enableDucking"]      = "Ducking aktiv",
        ["btnStart"]           = "Start",
        ["btnStop"]            = "Stopp",
        ["btnBypass"]          = "Bypass",
        ["btnProcess"]         = "Aktiv",
        ["panCenter"]          = "M",
        ["stoppedHint"]        = "Gestoppt. Wähle eine ungenutzte Quelle (ein Ausgang, der nicht mit deinen Lautsprechern verbunden ist).",
        ["runCapturing"]       = "Eingang",
        ["runPlaying"]         = "Ausgabe",
        ["runLatency"]         = "Latenz",
        ["pickDevicesMsg"]     = "Bitte zuerst Eingangs- und Ausgangsgerät wählen.",
        ["audioStartErr"]      = "Audio konnte nicht gestartet werden:",
        ["btnSavePreset"]      = "Speichern",
        ["btnDeletePreset"]    = "Löschen",
        ["btnCancel"]          = "Abbrechen",
        ["dlgSaveTitle"]       = "Voreinstellung speichern",
        ["dlgSavePrompt"]      = "Name der Voreinstellung:",
        ["ttStart"]            = "Audio-Verarbeitung starten",
        ["ttStop"]             = "Audio-Verarbeitung stoppen",
        ["ttBypass"]           = "Normalizer + Limiter überspringen, rohes Audio hören (A/B-Vergleich)",
        ["ttProcess"]          = "Normalizer + Limiter wieder einschalten",
        ["ttLatency"]          = "Puffergröße. Niedriger = weniger Verzögerung, höher = stabiler. Wirkt nach Stop + Start.",
        ["ledActive"]          = "duckt",
        ["ledIdle"]            = "ruhig",
        ["ledNoDiscord"]       = "Discord nicht gefunden",
        ["btnTest"]            = "Ducking testen",
        ["testRunning"]        = "Duckt…",
        ["testOk"]             = "✓ Gefunden",
        ["testFailed"]         = "✗ Kein Discord-Audio",
        ["ttTest"]             = "Klick: senkt Discords Lautstärke für 2 Sekunden, stellt sie dann wieder her. Discord öffnen und irgendein Audio abspielen (z.B. Mikrofontest) — dann hier klicken und hören / Discords Lautstärkeregler beim Runtergehen und Hochkommen beobachten.",
        ["labelDuckLive"]      = "Discord",
        ["ttLedEmpty"]         = "Keine App-Audio-Sessions sichtbar. Starte irgendwas mit Sound (Discord, Browser, …) damit Process-Namen hier auftauchen.",
        ["ttLedSeen"]          = "Aktuell sichtbare Audio-Sessions (wir ducken alles, dessen Name 'Discord', 'Vesktop', 'ArmCord', 'WebCord' oder 'Dorion' enthält):",
        ["diagDiscordDev"]     = "Discord spielt auf:",
        ["labelDiscordSrc"]    = "Discord spielt auf (optional — wird beim Funk leiser geregelt)",
        ["discordSrcNone"]     = "(keine — Windows per-App-Ducker nutzen)",
        ["labelAutoStart"]     = "PilotEars mit Windows starten",
        ["labelAutoEngage"]    = "Engine automatisch starten wenn vPilot/xPilot läuft",
        ["labelMinTray"]       = "Bei Minimieren in den System-Tray",
        ["labelStartMin"]      = "Minimiert starten",
        ["footerPre"]          = "Entwickelt mit ",
        ["footerPost"]         = " aus Gifhorn  ·  Thomas Kant",
    };

    private static string Truncate(string s, int max) => s.Length <= max ? s : s.Substring(0, max - 1) + "…";

    // ============== Device list live behavior ==============
    private bool _suppressDeviceSelectionEvent;

    private async void DeviceBox_DropDownOpened(object sender, EventArgs e)
    {
        // Refresh device list when user opens the dropdown — picks up newly
        // plugged-in devices (USB headsets, Bluetooth) without needing restart.
        // disableWhileLoading=false: don't toggle IsEnabled on an open popup
        // (WPF would close it the moment the control goes disabled).
        _suppressDeviceSelectionEvent = true;
        try { await LoadDevicesAsync(disableWhileLoading: false); }
        finally { _suppressDeviceSelectionEvent = false; }
    }

    private void DeviceBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loaded || _suppressDeviceSelectionEvent) return;
        if (!_engine.IsRunning) return;
        // User picked a different device while running — restart the engine
        // on the new pair. Brief audio drop is expected, like switching tracks.
        StopEngine();
        StartEngine(showErrors: false);
    }

    private void DiscordSourceBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loaded || _suppressDeviceSelectionEvent) return;
        // Prime the ducker's device id immediately so the live Discord meter
        // works even before any scan/start — driven straight from user choice.
        if (DiscordSourceBox.SelectedItem is DeviceItem item)
            _ducker.LastDiscordDeviceId = string.IsNullOrEmpty(item.Id) ? null : item.Id;
        // Re-decide per-app vs device-master duck based on new Discord device choice.
        ApplyAllToDucker();
        PersistSettings();
    }

    private void HelpButton_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new HelpDialog(_lang) { Owner = this };
        dlg.ShowDialog();
    }

    private async void DiscordAutoBtn_Click(object sender, RoutedEventArgs e)
    {
        // Retry a handful of times — Discord's session can be transiently
        // inactive at the moment of any single scan.
        DiscordAutoBtn.IsEnabled = false;
        var originalLabel = DiscordAutoBtn.Content;
        DiscordAutoBtn.Content = "…";

        // Clear the lock so the scan can look at ALL devices and detect Discord
        // wherever it actually plays. Without this, the scan would be filtered
        // to whatever was previously picked and never discover anything new.
        var previouslyLockedId = _ducker.LastDiscordDeviceId;
        _ducker.LastDiscordDeviceId = null;

        bool found = false;
        try { found = await _ducker.ScanWithRetryAsync(attempts: 4, delayMs: 150); }
        finally
        {
            DiscordAutoBtn.Content = originalLabel;
            DiscordAutoBtn.IsEnabled = true;
            // If detection failed, restore the previous lock so we don't lose
            // the user's old choice (they may have clicked Auto by mistake).
            if (!found && _ducker.LastDiscordDeviceId is null)
                _ducker.LastDiscordDeviceId = previouslyLockedId;
        }

        var detected = _ducker.LastDiscordDeviceNames;
        if (!found || string.IsNullOrEmpty(detected))
        {
            MessageBox.Show(this,
                _lang == "DE"
                    ? "Discord wurde nicht erkannt — bitte erst Discord öffnen und einen Sound abspielen (z.B. Mikrofontest). Falls Discord schon spielt, prüfe ob's wirklich aktuell Audio rausgibt."
                    : "Discord wasn't detected — open Discord and play any sound first (e.g. mic test). If Discord is already playing, verify it's actually producing audio right now.",
                "PilotEars", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        // pick first comma-separated name and find matching ComboBox item
        var firstName = detected.Split(',', StringSplitOptions.RemoveEmptyEntries)[0].Trim();
        if (DiscordSourceBox.ItemsSource is IEnumerable<DeviceItem> items)
        {
            var match = items.FirstOrDefault(d => d.FriendlyName == firstName);
            if (match is not null)
            {
                DiscordSourceBox.SelectedItem = match;
                // SelectionChanged only fires when the value ACTUALLY changes.
                // If the dropdown was already on this device, the handler doesn't
                // run — so we set LastDiscordDeviceId explicitly here to make sure
                // the live peak meter has the right device id, regardless.
                _ducker.LastDiscordDeviceId = match.Id;

                // warn if this would feed our own output back into itself
                if (OutputDeviceBox.SelectedItem is DeviceItem outItem && outItem.Id == match.Id)
                {
                    MessageBox.Show(this,
                        _lang == "DE"
                            ? "⚠ Discord-Quelle ist dasselbe Gerät wie deine Ausgabe — das würde einen Feedback-Loop erzeugen. Bitte ändere die Discord-Quelle oder die Ausgabe."
                            : "⚠ Discord source is the same as your output — this would create a feedback loop. Pick a different device for one of them.",
                        "PilotEars", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
        }
    }

    private async void TestDuckButton_Click(object sender, RoutedEventArgs e)
    {
        var t = Strings(_lang);
        TestDuckButton.IsEnabled = false;
        var originalContent = TestDuckButton.Content;

        // Start the 2-second forced duck in the background
        var testTask = _ducker.ForceDuckAsync(2000);

        // Visible countdown while it runs
        for (int s = 2; s > 0; s--)
        {
            TestDuckButton.Content = $"{t["testRunning"]} {s}";
            await Task.Delay(1000);
        }

        // Wait for the task to fully finish (release tail + restore) and get the count
        int sessions = await testTask;

        // Show outcome briefly
        if (sessions == 0)
        {
            TestDuckButton.Content = t["testFailed"];
            TestDuckButton.Foreground = new SolidColorBrush(Color.FromRgb(0xf9, 0x73, 0x16));
        }
        else
        {
            TestDuckButton.Content = $"{t["testOk"]} {sessions}";
            TestDuckButton.Foreground = new SolidColorBrush(Color.FromRgb(0x4a, 0xde, 0x80));
        }
        await Task.Delay(2500);

        // Restore button
        TestDuckButton.Content = originalContent;
        TestDuckButton.Foreground = (Brush)FindResource("TextPrimary");
        TestDuckButton.IsEnabled = true;
    }
}
