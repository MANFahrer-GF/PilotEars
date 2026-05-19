# PilotEars

**Real-time audio polishing for VATSIM vPilot / xPilot.**

PilotEars sits between vPilot/xPilot and your headset. It normalises quiet and
loud pilots to a consistent level, hard-caps sudden peaks, optionally pans the
radio audio, and automatically ducks Discord when ATC is speaking — so you can
fly on VATSIM with Discord open and never get blasted out of your chair.

No drivers, no virtual cables. Uses Windows WASAPI loopback.

![screenshot placeholder](docs/screenshot.png)

## Features

- **Normalizer (AGC)** — quiet pilots come up, loud ones come down. Target -18 dB by default (broadcast standard).
- **Brick-wall limiter** — peak ceiling with optional look-ahead. No clicks on screamers.
- **Pan** — place ATC anywhere from full-left to full-right.
- **Discord auto-ducking** — multi-path: per-app Windows volume + device master + mute. Works even on USB conference speakerphones (Anker, Jabra, Yealink, Poly…) whose hardware DSPs ignore normal per-app controls.
- **Optional mixer mode** — capture Discord from any device via WASAPI loopback and mix it into PilotEars's output, with smooth ducking applied inside the mix.
- **Live level meters** — input / AGC gain / output / Discord, all live at 30 fps.
- **Four presets + custom presets** — VATSIM (recommended), Live (low-latency), Aggressive, Minimal. Save your own with the `+` button.
- **DE/EN** — fully localized.

## Quick start

1. **Pick an unused render device** in Windows Sound — any device you don't actively listen on (Realtek Digital Output, unused HDMI, off Bluetooth speaker).
2. **vPilot → Settings → Audio → Speaker Device** → pick that unused device.
3. **Launch PilotEars.** Set:
   - **Source** = the unused device from step 1
   - **Output** = your real headset / speakers
   - Click **VATSIM** preset
4. **Click Start.** vPilot audio now flows through PilotEars into your headset, normalised and limited.

For Discord ducking: open Discord, play any sound, click the **Auto** button next to the Discord-source dropdown. PilotEars auto-detects Discord's device and configures ducking. Use the **Test** button to verify Discord audibly drops.

The `?` button in the app opens a more detailed help dialog in DE/EN.

## Requirements

- Windows 10 / 11
- .NET 8 Desktop Runtime (or use the self-contained build)
- vPilot or xPilot
- Two distinct audio render devices

## Build from source

```powershell
dotnet build -c Release
# or to produce a single-file publish:
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true
```

Output: `bin/Release/net8.0-windows/win-x64/publish/PilotEars.exe`

## Why this exists

VATSIM radio is unfiltered. Pilots transmit anywhere from -40 dB (whispers) to
peaking the digital ceiling (screamers). Chasing the volume slider every
transmission is a chore. PilotEars does it automatically while you fly.

This project was inspired by Katie's *KatiePilot Audio Normaliser* (a closed-source
binary tool) — same problem domain, completely independent implementation, with
additional features (Discord ducking, pan, mixer-mode, presets, live meters,
localization).

## License

MIT — see [LICENSE](LICENSE).

---

Entwickelt mit ♥ aus Gifhorn · Thomas Kant
