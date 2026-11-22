# DriveDeck

**A configurable car dashboard and loop dashcam for Windows 10 Mobile phones** (built and tested on a Nokia Lumia 930, OS build 10.0.14393).
It gives an old Lumia a second life on the windscreen: live engine data from a cheap Bluetooth ELM327 OBD-II adapter, fault-code alerts, and a dashcam with GPS speed stamp and automatic incident saving.

> DriveDeck is a hobby project. It is not affiliated with Microsoft, Nokia, SEAT, Volkswagen Group or OBDeleven.

## Features

### Dashboard
- Live OBD-II data through a **Bluetooth (classic/SPP) ELM327** adapter: rpm, speed, load, throttle, pedal, MAP/boost, MAF, ignition timing, coolant / intake / oil / ambient temperature, fuel trims, fuel rate, rail pressure, ECU and battery voltage, fuel level, instant consumption, plus GPS speed/altitude and phone G-force.
- **Custom values**: your own read-only requests (e.g. manufacturer UDS `22 xx xx` on a chosen ECU header) with a simple formula such as `(A*256+B)/10`.
- **Configurable layout**: up to 8 swipeable pages, a 1–6 × 1–6 grid per page, separate pages for landscape and portrait; tiles can be a number, dial gauge, bar, graph, camera preview, clock or status, each with its own span, colour, scale and warning/alarm thresholds.
- **Fault-code alerts**: stored, pending and (optionally) permanent codes from the engine and gearbox, checked periodically and immediately when the check-engine lamp turns on. New faults appear in a thin notification strip that never covers the tiles and collapses into a small badge.

### Dashcam
- Loop recording in 30 s – 5 min segments, 720p/1080p, 24/30 fps, adjustable bitrate, optional sound.
- **Storage cap slider or Unlimited**, always keeping a free-space reserve for the phone; the oldest clips are overwritten first.
- GPS **speed burned into the video** (optional date/time, coordinates, custom label) and/or a **subtitle track (.srt)** next to each clip, plus a **.gpx** track. Each can be turned off.
- **Impact detection** with the accelerometer (presets or custom g threshold with a live meter): footage from before and after the impact (default 60 s / 30 s) is moved to a protected *Events* folder with an `incident.txt`. A button saves incidents manually.
- Optional emergency-call prompt after an impact (the phone asks before dialling).
- Pauses on overheating and resumes when the phone cools down; built-in recordings browser with subtitle playback.

## Install on a phone

**➡ Step-by-step guide: [INSTALL.md](INSTALL.md)** (requirements, two install methods, first start, connecting the car, updating, uninstalling, troubleshooting).

In short:
1. Download `DriveDeck_<version>_ARM.zip` from [Releases](../../releases) and unzip it on a Windows PC.
2. On the phone: *Settings › Update & security › For developers* – turn on **Developer mode** and **Device discovery**; phone and PC on the same Wi-Fi.
3. Run `install-to-phone.ps1` (needs the Windows 10 SDK) or upload the package through the phone's **Device Portal** in a browser, and pair with the code the phone shows.
4. Optionally pair a classic-Bluetooth ELM327 (PIN `1234`/`0000`) and select it in *DriveDeck › Settings › OBD-II*.

Recordings are stored in `Videos\DriveDeck` (visible over USB). A diagnostic log, `Videos\DriveDeck\DriveDeck-log.txt`, contains app events and errors only.

## Project status

| Area | Status |
|---|---|
| Dashboard UI, layout editor, settings | Runs on a Lumia 930 (Windows 10 Mobile 1607) |
| Dashcam: loop recording, burned-in stamp, .srt/.gpx, storage limit | Verified on a Lumia 930 (1080p clips checked on a PC) |
| Impact detection, incident saving | Implemented and unit-tested; not yet verified in a real impact (hopefully never) |
| OBD-II via ELM327 | Implemented and tested against a simulated ELM327/CAN car (94 unit tests); **not yet tested in a real car** – feedback welcome |

## Build from source

Requirements: Windows 10/11, **Visual Studio 2022** with the *Universal Windows Platform development* workload and **Windows 10 SDK 10.0.19041**, and the **.NET SDK** (for the tests).

```powershell
.\build.ps1
```

The script runs the unit tests, creates a local self-signed signing certificate (`CN=DriveDeck Dev`) on first use, and builds `out\DriveDeck.App_<version>_ARM_Test\`. The app targets Windows 10 Mobile 1607 (14393) and uses .NET Native 1.7 (`Microsoft.NETCore.UniversalWindowsPlatform` 5.4.x), the last toolchain that supports that OS version.

Project layout:

| Path | Contents |
|---|---|
| `src/Core` | Platform-independent logic: ELM327 parser (ISO-TP), read-only command guard, formula language, fault-code decoding, OBD polling, loop-storage policy, impact detector, stamp/SRT/GPX writers, settings validation |
| `src/App` | UWP app: dashboard, settings, layout editor, recordings, Bluetooth transport, dashcam service |
| `src/Effects` | GPU video effect (Win2D) that burns the stamp into recorded frames |
| `tests/Core.Tests` | 94 unit tests, including a simulated ELM327 on a CAN car |

## Limitations

- Windows 10 Mobile suspends apps that leave the screen: keep DriveDeck in the foreground while driving (it keeps the screen on).
- There is a short gap (under a second) between loop segments.
- The burned-in stamp is laid out for a landscape mount.
- Generic OBD-II shows emission-related codes of the engine and gearbox only; other control units need a manufacturer tool.
- Only classic-Bluetooth ELM327 adapters are supported (not Bluetooth LE or Wi-Fi adapters).
- Developed on a Lumia 930 with a SEAT Leon Mk4 (CAN) in mind; other phones and cars may need tweaks. Reports and pull requests are welcome.

## Safety, privacy and legal

- **Do not operate the phone while driving.** Mount it so it does not block your view, and follow local law on windscreen mounts.
- **Read-only by design:** only OBD read services (01, 02, 03, 07, 09, 0A, 19 sub-functions 01/02/0A, 21, 22) can be sent. Clearing codes, coding, ECU resets, security access, actuator tests and flashing are blocked in code, and requests must be plain hex so nothing can be injected into the adapter.
- **No internet permission:** the app sends nothing anywhere. Location is stored only in your own video sidecar files.
- **Unplug the ELM327 when you leave the car** – cheap clones accept any Bluetooth connection with a default PIN.
- Dashcam and audio-recording rules differ by country (e.g. recording passengers' voices, publishing footage with faces or number plates). You are responsible for using recordings lawfully.
- Provided "as is", without warranty – see [LICENSE](LICENSE).

## License

[MIT](LICENSE) © 2026 Máca
