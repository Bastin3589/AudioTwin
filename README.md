# AudioTwin

A tiny Windows utility that plays your normal Windows audio through **two playback devices at the same time** — for example a Bluetooth headset *and* the 3.5 mm speakers.

No virtual cables. No Voicemeeter. No mixer. One job, and it does it from the notification area — the app has no taskbar button, just a tray icon next to the clock.

```
┌──────────────────────────────────────┐
│              AudioTwin               │
│                                      │
│ Input:  Windows Default Audio        │
│                                      │
│ Output 1: [ Bluetooth Headphones ▼ ] │
│ Volume:   [████████████████──] 100%  │
│ Delay:    [──────────────────]  0 ms │
│                                      │
│ Output 2: [ Speakers / AUX ▼ ]       │
│ Volume:   [████████████████──] 100%  │
│ Delay:    [───█──────────────] 120ms │
│                                      │
│         [ START ROUTING ]            │
│                                      │
│ Status: ● Routing                    │
│                                      │
│      [ Hide to notification area ]   │
└──────────────────────────────────────┘

              closes to ↓

   ┌───────────────────────────────┐
   │  ▲ ⌃ ☰ 🔊 ⏻   12:04  02/09/26 │   ● = routing
   └───────────────────────────────┘
```

---

## Architecture

```
Windows default playback endpoint
              │
   WasapiLoopbackCapture            32-bit float PCM at the device mix rate
              │
      ┌───────┴───────┐             one capture callback, two consumers
      ▼               ▼
 BufferedWaveProvider   BufferedWaveProvider      jitter buffer (per output)
      │               │
 DelaySampleProvider  DelaySampleProvider         0–500 ms lip-sync trim
      │               │
 VolumeSampleProvider VolumeSampleProvider        independent gain
      │               │
 WdlResampling…       WdlResampling…              only if rates differ
      │               │
 ChannelMap…          ChannelMap…                 only if channel counts differ
      │               │
   WasapiOut          WasapiOut                   shared mode, event-driven
      │               │
  Device A          Device B
```

**Why this shape:**

* **WASAPI loopback capture** is the only way to get "whatever Windows is playing" without a kernel driver or a virtual cable. It attaches to an existing render endpoint and hands back the post-mix stream — no VB-CABLE, no Voicemeeter, and the Windows default device is never changed.
* **Two fully independent branches.** Each output owns its own buffer, clock and `WasapiOut` instance, so a Bluetooth device stalling or vanishing cannot disturb the wired one. Sample rate and channel count are negotiated per device, so a 48 kHz stereo capture can feed a 44.1 kHz headset and a 48 kHz 5.1 receiver at once.
* **Shared mode, not exclusive.** Exclusive mode would lock other apps out of the device and fails constantly with Bluetooth. Shared mode is the reliable choice.
* **Float PCM end to end.** The capture format is already 32-bit float; it stays float through delay, volume and (if needed) resampling. Nothing is compressed, and resampling only happens when a device genuinely runs at a different rate.
* **Drift guard instead of clock sync.** Two endpoints run on two crystals, so one will slowly outrun the other. Rather than pretending to resync clocks, each branch drops the oldest audio if its queue grows past 250 ms. In practice this fires rarely and is inaudible; latency can never creep upward over hours.
* **NAudio** is used for endpoint enumeration, loopback capture, WASAPI render, the WDL resampler and the volume provider. Everything specific to this tool (delay line, channel mapping, fan-out, device recovery) is in this repo.

### The one behaviour worth knowing about

If you pick the **Windows default device itself** as one of the two outputs, that branch switches to **pass-through**: it does nothing, because Windows is already playing to that device natively. This is not laziness — rendering into the same endpoint we are capturing from creates a feedback loop, where our own output is captured and re-rendered, over and over.

So the normal way to use the app is:

* Windows default output = e.g. the Bluetooth headset (this one plays natively).
* Output 1 = that same headset → shown as *"already playing natively"*.
* Output 2 = the 3.5 mm speakers → actively fed by the duplicator.

Or set the default to something else and let the app drive both outputs, in which case both get independent volume and delay control. The UI tells you which mode each output is in.

---

## Requirements

* Windows 10 (1809 or newer) or Windows 11
* [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) — **only to build**. The published `.exe` is self-contained and needs nothing installed.

## Getting a runnable .exe

### Option 1 — double-click `build.cmd` (easiest)

Unzip the project, double-click **`build.cmd`**, wait about a minute, and answer `Y` when it offers to run the app.

The script picks x64 or ARM64 automatically, offers to install the .NET 8 SDK through `winget` if it isn't present, and drops a single standalone file at:

```
publish\AudioTwin.exe
```

That one file is the whole application. Copy it to any Windows 10/11 machine — no runtime, no installer, no dependencies.

### Option 2 — let GitHub build it for you

Push the project to a GitHub repository. The included workflow (`.github/workflows/build.yml`) builds x64 and ARM64 executables on a Windows runner and attaches them as downloadable artifacts, so you never install anything locally. Tag a commit `v1.0.0` and the binaries are attached to a release automatically.

### Option 3 — Visual Studio

Open `AudioTwin.sln` in Visual Studio 2022 (with the *.NET desktop development* workload) and press F5.

### Option 4 — command line

```powershell
cd AudioTwin
dotnet run --project src\AudioTwin\AudioTwin.csproj
```

## Publish commands (what `build.cmd` runs)

**Self-contained** — a single `.exe` that runs on any Windows 10/11 x64 machine with no .NET installed (about 70–90 MB):

```powershell
dotnet publish src\AudioTwin\AudioTwin.csproj `
  -c Release `
  -r win-x64 `
  --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true `
  -o publish
```

**Framework-dependent** — a much smaller `.exe` that needs the .NET 8 Desktop Runtime installed (about 1 MB):

```powershell
dotnet publish src\AudioTwin\AudioTwin.csproj `
  -c Release `
  -r win-x64 `
  --self-contained false `
  -p:PublishSingleFile=true `
  -o publish
```

For ARM64 machines replace `win-x64` with `win-arm64`.

The result is `publish\AudioTwin.exe`. It is fully portable — copy it anywhere. Settings live in `%AppData%\AudioTwin\settings.json`.

---

## Using it

1. Launch the app.
2. **Output 1** and **Output 2**: pick the two playback devices you want.
3. Press **START ROUTING**.
4. Adjust **Volume** per output if one device is louder than the other.
5. If the Bluetooth side lags behind the wired side, raise the **Delay** on the *wired* output until they line up (Bluetooth is typically 100–250 ms behind).

6. Close the window when you are done. Routing keeps running; the icon stays in the tray.

Optional checkboxes:

* **Start routing when application launches** — begins routing automatically on start-up, using the last-used devices.
* **Start with Windows** — adds a per-user entry to `HKCU\...\CurrentVersion\Run`, invoked with `--tray` so a machine start-up never throws a window at you. No admin rights needed.
* **Start hidden in the notification area** — the same, for manual launches.
* **Closing this window hides it instead of quitting** — on by default. Turn it off if you would rather have the X button really quit.

## Living in the notification area

The window is a settings panel, not the application. The application is the tray icon.

* **No taskbar button, ever.** The window is created with `ShowInTaskbar = false`, so it also stays out of Alt+Tab. The tray icon is the only handle on the app.
* **Left-click the icon** to open the settings window. **Right-click** for a menu: open, start/stop routing, toggle *Start with Windows*, exit.
* **Closing or minimising the window hides it.** Routing is untouched — that is the whole point. The first time this happens, a balloon tip says so, once.
* **Exit** — from the tray menu only, or by unticking *closing this window hides it* and using the X.
* **The icon carries the state:** grey = stopped, green = routing, amber = routing with one output down. Hover for the same in words.
* **Launching the app a second time** doesn't start a second copy and doesn't nag with a dialog — it just brings the existing window up, which is what a double-click on the .exe means when there is no taskbar button to click.
* **Failures speak up.** If a device dies while the window is hidden, or routing fails to start on launch, a balloon tip explains it instead of the app going quietly dead.

The tray icons are drawn in code at exactly the size Windows asks for (see `AppIcons.cs`), so they stay sharp at any DPI and never get scaled. The window and `.exe` icon is the AudioTwin logo from `AudioTwin.ico`, which ships every size from 16 to 256 pixels.

### Starting hidden

```powershell
AudioTwin.exe --tray
```

`--tray` (also `--hidden`, `--minimized`, `--background`, `--silent`) starts straight into the notification area with no window. Combine it with **Start routing when application launches** and the app is invisible: audio simply comes out of both devices from the moment you log in.

## Device handling

* The playback list refreshes automatically when a device is connected, disconnected, enabled or disabled.
* If an output device disappears while routing (Bluetooth headset walks out of range, powers off), that output stops and reports the error; **the other output keeps playing**. When the device comes back, the app reopens it automatically.
* If Windows switches its default playback device while routing, capture restarts on the new default.
* Changing an output selection while routing restarts routing immediately, so you can escape a dead device without stopping first.

## Latency notes

Bluetooth latency is imposed by the A2DP codec and the headset's own buffer; it cannot be removed, only compensated for. The delay sliders let you push the *faster* (wired) output back to match. Each branch adds roughly 100 ms of WASAPI render buffer plus whatever the jitter buffer holds — deliberately generous, because dropout-free playback matters more here than minimum latency.

If you hear crackling on a flaky Bluetooth link, raising `OutputLatencyMilliseconds` in `Audio/OutputBranch.cs` (100 → 150 or 200) is the single knob to turn.

## Troubleshooting

| Symptom | Cause / fix |
| --- | --- |
| "Output 1 and Output 2 must be different devices." | Both drop-downs point at the same endpoint. |
| One output shows *"already playing natively"* | That device is the Windows default. See the note above — this is intended, and its volume/delay sliders are disabled because Windows owns that stream. |
| No sound on either output | Check that something is actually playing to the Windows default device, and that the default device is not muted. Loopback capture only sees what Windows is mixing. |
| Echo or a rising howl | Should be impossible thanks to the pass-through rule, but if it happens, stop routing and make sure neither output is the default device. |
| "Could not open …" | The device is in exclusive-mode use by another application, or was disabled in Sound settings. |
| Audio drops out after minutes/hours | Power management for the USB/Bluetooth adapter. Disable "Allow the computer to turn off this device to save power" in Device Manager. |
| The window disappeared | It was hidden, not closed. Click the tray icon next to the clock. If the icon is tucked away, drag it out of the `^` overflow in Windows 11's taskbar settings. |
| I can't find it in Alt+Tab | By design — it is a tray application and has no taskbar button. Use the tray icon. |
| I want it to really quit | Right-click the tray icon → **Exit**, or untick *closing this window hides it instead of quitting*. |
| No tray icon at all | Windows hides new tray icons by default. Settings → Personalisation → Taskbar → *Other system tray icons*. |

## What this app deliberately does not do

No EQ, no microphone mixing, no recording, no effects, no virtual cables, no routing matrix, no plugins, no taskbar clutter. One stream, two devices, volume and delay, from a tray icon. That's all.

## Project layout

```
AudioTwin.sln
README.md
build.cmd                           one-click build -> publish\AudioTwin.exe
.github/workflows/build.yml         cloud build (x64 + ARM64) via GitHub Actions
src/AudioTwin/
  AudioTwin.csproj                  net8.0-windows, WinForms, NAudio 2.2.1
  Program.cs                        entry point, single-instance guard, --tray switch
  MainForm.cs                       the entire UI + notification area (no designer)
  AppIcons.cs                       state tray icons (drawn), window icon (logo)
  AudioTwin.ico                     app logo, 16-256 px, used for the .exe too
  AppSettings.cs                    JSON settings in %AppData%
  StartupManager.cs                 "Start with Windows" registry entry
  Audio/
    AudioRouter.cs                  loopback capture + fan-out to two branches
    OutputBranch.cs                 one output device pipeline
    DelaySampleProvider.cs          adjustable circular delay line
    ChannelMapSampleProvider.cs     mono/stereo/multichannel adaptation
    DeviceWatcher.cs                enumeration + hot-plug notifications
    AudioDeviceInfo.cs              detached device descriptor
```

## License

MIT — do whatever you like with it.
