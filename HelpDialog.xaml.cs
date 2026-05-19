using System.Windows;
using System.Windows.Controls;

namespace PilotEars;

public partial class HelpDialog : Window
{
    public HelpDialog(string lang)
    {
        InitializeComponent();
        Title = lang == "DE" ? "PilotEars — Hilfe" : "PilotEars — Help";
        CloseBtn.Content = lang == "DE" ? "Schließen" : "Close";
        BuildContent(lang);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void BuildContent(string lang)
    {
        var sections = lang == "DE" ? GermanContent : EnglishContent;
        foreach (var (style, text) in sections)
        {
            var tb = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
            if (style is not null)
            {
                if (FindResource(style) is Style s) tb.Style = s;
            }
            ContentPanel.Children.Add(tb);
        }
    }

    private static readonly (string? style, string text)[] EnglishContent = new[]
    {
        ((string?)"H1", "PilotEars"),
        ((string?)"Muted", "v1.1 — Real-time audio polishing for VATSIM vPilot / xPilot"),

        ((string?)"H2", "What it does"),
        (null, "PilotEars sits between vPilot / xPilot and your headset. It listens to the radio audio in real time and:"),
        (null, "•  Normalises quiet vs. loud pilots to a consistent level"),
        (null, "•  Hard-caps sudden peaks (Brick-wall Limiter)"),
        (null, "•  Places ATC audio left/center/right (Pan)"),
        (null, "•  Automatically lowers Discord's volume during ATC speech, then restores it"),
        (null, "No drivers, no virtual cables — uses Windows WASAPI loopback."),

        ((string?)"H2", "Setup (4 steps)"),
        ((string?)"H3", "1. Pick an unused output device"),
        (null, "PilotEars needs a render device you don't actively listen on. Examples: Realtek Digital Output (S/PDIF), HDMI from an unused monitor, an off Bluetooth speaker. The point: vPilot routes audio there, PilotEars taps it via loopback without you hearing it directly."),

        ((string?)"H3", "2. Tell vPilot to output there"),
        (null, "vPilot → Settings → Audio → Speaker Device: pick the unused device."),

        ((string?)"H3", "3. Configure PilotEars"),
        (null, "Source: the unused device from step 1. Output: your real headset / speakers. Click a preset (VATSIM recommended)."),

        ((string?)"H3", "4. Click Start"),
        (null, "Audio flows: vPilot → unused output → PilotEars → your headset."),

        ((string?)"H2", "Discord auto-ducking"),
        (null, "After PilotEars is running, lower Discord during ATC speech automatically:"),
        (null, "•  Open Discord and play any sound (Settings → Voice & Video → 'Let's check' loops your mic)"),
        (null, "•  In PilotEars: click the 'Auto' button next to the Discord-source dropdown. It detects Discord's output device and configures itself"),
        (null, "•  Trigger threshold: -34 dB is a good start. Duck amount: 80–100% for strong reduction"),
        (null, "•  The orange LED in the Discord section lights when ducking is active"),
        (null, "•  The Test button forces a 2-second duck so you can verify Discord audibly drops"),
        (null, "PilotEars uses two reduction paths automatically: per-app volume control (works for normal devices) and a device-level master volume + mute (works for USB conference speakers like Anker / Jabra that ignore per-app volume)."),

        ((string?)"H2", "The four presets"),
        (null, "VATSIM (recommended): Target -18 dB, Ceiling -3 dB, Release 200 ms, Look-ahead 150 ms, Latency 170 ms. Broadcast-quality smooth. The reference setup."),
        (null, "Live: Target -15, Look-ahead 5 ms. Low-latency variant — for users who want minimal delay over absolute smoothness."),
        (null, "Aggressive: Target -12, faster release. For extreme dynamic-range traffic where quiet pilots need heavy boost."),
        (null, "Minimal: light processing, zero added latency. If PilotEars feels too 'baked'."),
        (null, "Save your own presets with the + button. The active preset is highlighted blue; manually tweaking any slider clears the highlight."),

        ((string?)"H2", "Live levels — reading the meters"),
        (null, "Input (blue): how loud the raw vPilot audio is. Should peak around -12 to -6 dB during transmissions."),
        (null, "AGC gain (green): how much the Normalizer is adjusting. Positive = boosting quiet pilots, negative = taming loud ones."),
        (null, "Output (orange): what actually goes to your headset. Should stay relatively constant around the Target loudness."),
        (null, "Discord (purple): Discord's current playback peak — shows whether Discord is alive."),
        (null, "If Output is constant while Input swings wildly, the Normalizer is doing its job."),

        ((string?)"H2", "Bypass vs Stop"),
        (null, "Stop: ends audio capture entirely. No audio flows through PilotEars."),
        (null, "Bypass: engine keeps running but Normalizer + Limiter are skipped — you hear raw vPilot audio. A/B compare with vs. without processing."),

        ((string?)"H2", "Troubleshooting"),
        ((string?)"H3", "I don't hear anything"),
        (null, "Check: vPilot's Speaker Device matches PilotEars's Source. The Input meter must wiggle when ATC speaks. If it doesn't, your routing is wrong."),

        ((string?)"H3", "Discord doesn't get quieter"),
        (null, "First check: is your trigger threshold too high? If it's at -5 dB but your Input peaks at -20, the threshold is never crossed. Lower it to -34 dB."),
        (null, "Then verify with the Test button — Discord should visibly drop in the Windows Volume Mixer."),
        (null, "If Test works but real triggers don't: lower threshold further or increase duck amount."),
        (null, "If Test doesn't work either: your Discord might be on a different device than where you listen — see next item."),

        ((string?)"H3", "Discord and headset on the same device"),
        (null, "Setup: PilotEars Output = your headset (where you listen). Discord-source dropdown = (none). Ducking happens via per-app + device-master, automatically."),

        ((string?)"H3", "Discord on a different device than your headset"),
        (null, "If you listen via PilotEars's output: pick that other device in 'Discord plays on' (or click Auto). PilotEars mixes Discord into your output and ducks it there."),
        (null, "If you listen via Discord's own device (e.g. USB speakerphone): leave Discord-source as (none). Per-app + device-master will lower volume on that device directly."),

        ((string?)"H3", "Audio is choppy / glitches"),
        (null, "Increase Latency to 60 or 100 ms. Takes effect after Stop + Start."),

        ((string?)"H3", "I hear clicks on loud transmissions"),
        (null, "Increase Look-ahead (5 → 20 ms). The limiter catches peaks more cleanly."),
    };

    private static readonly (string? style, string text)[] GermanContent = new[]
    {
        ((string?)"H1", "PilotEars"),
        ((string?)"Muted", "v1.1 — Echtzeit-Audio-Polishing für VATSIM vPilot / xPilot"),

        ((string?)"H2", "Was das Tool macht"),
        (null, "PilotEars sitzt zwischen vPilot / xPilot und deinem Headset. Es lauscht in Echtzeit auf den Funk und:"),
        (null, "•  Gleicht leise und laute Piloten an (Normalizer)"),
        (null, "•  Kappt plötzliche Peaks (Brick-Wall-Limiter)"),
        (null, "•  Platziert ATC-Audio links/mitte/rechts (Panorama)"),
        (null, "•  Senkt Discords Lautstärke automatisch wenn ATC spricht und hebt sie wieder"),
        (null, "Keine Treiber, keine virtuellen Kabel — nutzt Windows WASAPI-Loopback."),

        ((string?)"H2", "Einrichtung (4 Schritte)"),
        ((string?)"H3", "1. Ungenutzten Audio-Ausgang wählen"),
        (null, "PilotEars braucht ein Render-Gerät, das du AKTIV NICHT hörst. Beispiele: Realtek Digital Output (S/PDIF), HDMI von einem nicht angeschlossenen Monitor, ein abgeschalteter Bluetooth-Speaker. Der Trick: vPilot routet seinen Sound dorthin, PilotEars greift's per Loopback ab ohne dass du's direkt hörst."),

        ((string?)"H3", "2. vPilot dorthin routen"),
        (null, "vPilot → Einstellungen → Audio → Speaker Device: das ungenutzte Gerät wählen."),

        ((string?)"H3", "3. PilotEars konfigurieren"),
        (null, "Quelle: das Gerät aus Schritt 1. Ausgabe: dein echtes Headset / Lautsprecher. Eine Voreinstellung klicken (VATSIM empfohlen)."),

        ((string?)"H3", "4. Start klicken"),
        (null, "Audio fließt: vPilot → ungenutzter Ausgang → PilotEars → dein Headset."),

        ((string?)"H2", "Discord Auto-Ducking"),
        (null, "Wenn PilotEars läuft, Discord automatisch beim ATC-Funk leiser machen:"),
        (null, "•  Discord öffnen und irgendein Audio abspielen (Einstellungen → Sprache & Video → 'Lass uns mal überprüfen' loopt dein Mikro)"),
        (null, "•  In PilotEars: 'Auto'-Knopf neben dem Discord-Quelle-Dropdown klicken. Erkennt Discords Ausgabegerät automatisch."),
        (null, "•  Auslöseschwelle: -34 dB ist guter Start. Duck-Stärke: 80–100% für deutliche Absenkung."),
        (null, "•  Die orange LED in der Discord-Section leuchtet wenn gerade geduckt wird."),
        (null, "•  Der Test-Knopf erzwingt 2 Sekunden Ducking damit du hörst/siehst dass Discord wirklich leiser wird."),
        (null, "PilotEars nutzt automatisch zwei Wege gleichzeitig: per-App-Volume (für normale Geräte) und Geräte-Master-Volume + Mute (für USB-Konferenz-Speaker wie Anker / Jabra, die per-App ignorieren)."),

        ((string?)"H2", "Die vier Voreinstellungen"),
        (null, "VATSIM (empfohlen): Target -18 dB, Ceiling -3 dB, Release 200 ms, Look-ahead 150 ms, Latenz 170 ms. Broadcast-Qualität, sanft. Der Referenz-Setup."),
        (null, "Live: Target -15, Look-ahead 5 ms. Low-Latency-Variante — wenn dir Reaktionszeit wichtiger ist als absolute Glätte."),
        (null, "Aggressive: Target -12, schneller Release. Für extremen Dynamikumfang wenn leise Piloten kräftig geboostet werden müssen."),
        (null, "Minimal: leichte Verarbeitung, null Zusatzlatenz. Wenn PilotEars dir zu stark bearbeitet klingt."),
        (null, "Eigene Presets mit dem +-Knopf speichern. Das aktive Preset wird blau hervorgehoben; jeder manuelle Slider-Tweak löscht die Markierung."),

        ((string?)"H2", "Live-Pegel — die Anzeigen lesen"),
        (null, "Eingang (blau): wie laut das rohe vPilot-Audio reinkommt. Sollte bei Funksprüchen um -12 bis -6 dB peaken."),
        (null, "AGC-Gain (grün): wieviel der Normalizer gerade dreht. Positiv = boostet leise Piloten, negativ = drosselt laute."),
        (null, "Ausgang (orange): was tatsächlich an dein Headset geht. Sollte ziemlich konstant rund um die Ziel-Lautstärke bleiben."),
        (null, "Discord (lila): Discords aktueller Wiedergabe-Pegel — zeigt ob Discord lebt."),
        (null, "Wenn Ausgang konstant bleibt während Eingang wild schwankt, macht der Normalizer seinen Job."),

        ((string?)"H2", "Bypass vs. Stopp"),
        (null, "Stopp: beendet die Audio-Verarbeitung komplett. Kein Audio fließt mehr durch PilotEars."),
        (null, "Bypass: Engine läuft weiter, aber Normalizer + Limiter werden übersprungen — du hörst das rohe vPilot-Audio. A/B-Vergleich mit/ohne Verarbeitung."),

        ((string?)"H2", "Problemlösungen"),
        ((string?)"H3", "Ich höre nichts"),
        (null, "Prüfe: vPilots Speaker Device = PilotEars Quelle. Der Eingangs-Meter muss wackeln wenn ATC spricht. Wenn nicht, stimmt dein Routing nicht."),

        ((string?)"H3", "Discord wird nicht leiser"),
        (null, "Erst prüfen: Auslöseschwelle zu hoch? Wenn sie auf -5 dB steht aber dein Eingang peakt bei -20, triggert nie. Senke auf -34 dB."),
        (null, "Dann verifizieren mit dem Test-Knopf — Discord sollte im Windows-Lautstärkemixer sichtbar runtergehen."),
        (null, "Wenn Test funktioniert, echte Trigger aber nicht: Schwelle weiter senken oder Duck-Stärke erhöhen."),
        (null, "Wenn auch Test nicht klappt: Discord könnte auf einem anderen Gerät als dein Headset spielen — siehe nächster Punkt."),

        ((string?)"H3", "Discord und Headset auf demselben Gerät"),
        (null, "Setup: PilotEars Ausgabe = dein Headset (wo du hörst). Discord-Quelle-Dropdown = (keine). Ducking läuft automatisch über per-App + Geräte-Master."),

        ((string?)"H3", "Discord auf anderem Gerät als Headset"),
        (null, "Wenn du über PilotEars-Ausgabe hörst: das andere Gerät im 'Discord spielt auf'-Dropdown wählen (oder Auto-Knopf). PilotEars mischt Discord in deine Ausgabe und duckt's dort."),
        (null, "Wenn du direkt über Discords Gerät hörst (z.B. USB-Speakerphone): Discord-Quelle auf (keine) lassen. Per-App + Geräte-Master regeln dann das Discord-Gerät direkt runter."),

        ((string?)"H3", "Audio ist abgehackt / glitcht"),
        (null, "Latenz auf 60 oder 100 ms erhöhen. Wirkt nach Stop + Start."),

        ((string?)"H3", "Ich höre Knacken bei lauten Funksprüchen"),
        (null, "Look-ahead erhöhen (5 → 20 ms). Der Limiter erwischt Peaks dann sauberer."),
    };
}
