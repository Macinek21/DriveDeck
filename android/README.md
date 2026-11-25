# DriveDeck for Android

Configurable OBD-II dashboard and loop dashcam for **Android 8.0 (Galaxy S7 with its last update) to Android 17**. Same features as the Windows 10 Mobile app, plus:

- keeps recording with the **screen off or another app (e.g. navigation) in front**, as a foreground service with *Save incident* and *Stop* buttons in the notification
- thresholds and fault codes are watched on all pages, not only the visible one
- share a saved video directly from *Recordings*
- 480p to 4K and 15 to 240 fps, as far as the phone's camera offers them to apps; H.264 or, if the phone has an encoder for it, H.265; 2-100 Mbit/s
- footage on the phone or on a **memory card**

## Install

1. Copy `DriveDeck-<version>.apk` to the phone and open it. Allow *install unknown apps* for the app you opened it with.
   If Play Protect warns about an unknown developer, choose *Install anyway* only if you got the file from this project's Releases page.
   On Samsung phones with **Auto Blocker** (One UI 6.1.1 and newer) it must be off while installing.
2. Open DriveDeck, turn the phone to landscape and allow camera, location (**precise**, *while using the app*), nearby devices and notifications; on Android 8-10 also files.
3. **Samsung:** *Settings > Apps > DriveDeck > Battery > Unrestricted*, otherwise One UI may stop it in the background.
4. Car: pair the ELM327 (classic Bluetooth, PIN 1234 or 0000) in Android's Bluetooth settings, then *⋮ > Settings > OBD-II > Adapter*. Unplug the adapter when you leave the car: clones accept anyone with the default PIN.

Update: install a newer APK over it. Recordings are in `Documents/DriveDeck` (`Loop`, `Events`, `DriveDeck-log.txt`) and survive uninstalling.
**After uninstalling and reinstalling on Android 11+**, Android hides the old files from the app: it can no longer play, count or overwrite them. Delete or move them with a file manager.

## Video modes and memory card

- *Settings* lists the modes from the phone's own information: the camera's normal and high-speed modes, and on Samsung phones with the older camera interface also 1080p60, 1440p60 and 4K30 as **experimental**: Samsung does not promise those to other apps. If the phone refuses one, DriveDeck says so, records the closest mode that works and removes it from the list. In these modes and in high speed a clip is ended by its size instead of its time, so its length varies, and recording moves on to the next one without stopping if the phone can do that. After an incident the clip is closed at once, which leaves a short gap.
- **High-speed and Samsung-interface modes** run without the burned-in stamp; speed stays in the `.srt` file. The real frame rate of the first clip is written to the log.
- **Memory card:** if it is missing, recording goes to the phone. On Android 11+ the folder is `Documents/DriveDeck` on the card. On Android 8-10 (Galaxy S7) it is `Android/media/io.github.macinek21.drivedeck/DriveDeck`, which **Android deletes when the app is uninstalled, including saved incidents**.
- Ejecting the card while recording on Android 8 makes Android kill the app: the current clip is lost, and DriveDeck restarts by itself on the phone. Stop recording before ejecting.
- The size limit counts the loop clips of phone and card together.

## Build

JDK 17+ and the Android SDK (platform 37): `./gradlew testDebugUnitTest lintRelease assembleRelease`.
Release signing reads `keystore.properties` (`storeFile`, `storePassword`, `keyAlias`, `keyPassword`), which is never committed.
Debug builds offer a *Simulated car* adapter; release builds do not contain it.

## Tested

| What | Where |
|---|---|
| Protocol, formulas, storage rules, video modes, impact detection, settings import | 33 unit tests |
| Memory card: recording, incident, nearly full, ejecting, putting back | Emulators: Android 8.0 and 17 |
| Recording, stamp, subtitles, background, screen off, simulated impact, saving, stop | Emulators: Android 8.0 and 17 |
| 1080p60, 1440p60, 4K30 on a Galaxy S7 (Android 8.0) | recorded at the full rate; long drives **not yet** |
| 720p120 and 720p240 on a Galaxy S7 | recorded at 117 and 170-185 fps |
| Moving on to the next clip without stopping (60 fps, 4K, high speed modes) | works on a Galaxy S7, tested for a few minutes per mode |
| Real phones, real cards, real impacts, heat, Bluetooth, a real car | **not yet** |

The gap between two clips was 0.1-1 s on the emulators (longer when the PC was overloaded). If the phone loses power or the app is killed, the clip being recorded at that moment is lost.

## Security and privacy

- No internet permission; a library cannot add one (removed in the manifest). No cloud backup of settings.
- Read-only towards the car: services 01, 02, 03, 07, 09, 0A, 19 (01/02/0A), 21, 22 only.
- Emergency button only opens the dialler, with a number from a fixed list.
- The log contains no positions or car data. Videos, `.srt`, `.gpx` and `incident.txt` do contain positions unless GPS is turned off, and other apps with file access can read them.
- The emergency and impact features are aids, not safety systems. Do not operate the phone while driving.
