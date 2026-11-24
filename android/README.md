# DriveDeck for Android

Configurable OBD-II dashboard and loop dashcam for **Android 8.0 (Galaxy S7 with its last update) to Android 17**. Same features as the Windows 10 Mobile app, plus:

- keeps recording with the **screen off or another app (e.g. navigation) in front**, as a foreground service with *Save incident* and *Stop* buttons in the notification
- thresholds and fault codes are watched on all pages, not only the visible one
- share a saved video directly from *Recordings*

## Install

1. Copy `DriveDeck-<version>.apk` to the phone and open it. Allow *install unknown apps* for the app you opened it with.
   If Play Protect warns about an unknown developer, choose *Install anyway* only if you got the file from this project's Releases page.
   On Samsung phones with **Auto Blocker** (One UI 6.1.1 and newer) it must be off while installing.
2. Open DriveDeck, turn the phone to landscape and allow camera, location (**precise**, *while using the app*), nearby devices and notifications; on Android 8-10 also files.
3. **Samsung:** *Settings > Apps > DriveDeck > Battery > Unrestricted*, otherwise One UI may stop it in the background.
4. Car: pair the ELM327 (classic Bluetooth, PIN 1234 or 0000) in Android's Bluetooth settings, then *⋮ > Settings > OBD-II > Adapter*. Unplug the adapter when you leave the car: clones accept anyone with the default PIN.

Update: install a newer APK over it. Recordings are in `Documents/DriveDeck` (`Loop`, `Events`, `DriveDeck-log.txt`) and survive uninstalling.
**After uninstalling and reinstalling on Android 11+**, Android hides the old files from the app: it can no longer play, count or overwrite them. Delete or move them with a file manager.

## Build

JDK 17+ and the Android SDK (platform 37): `./gradlew testDebugUnitTest lintRelease assembleRelease`.
Release signing reads `keystore.properties` (`storeFile`, `storePassword`, `keyAlias`, `keyPassword`), which is never committed.
Debug builds offer a *Simulated car* adapter; release builds do not contain it.

## Tested

| What | Where |
|---|---|
| Protocol, formulas, storage rules, impact detection, settings import | 29 unit tests |
| Recording, stamp, subtitles, background, screen off, simulated impact, saving, stop | Emulators: Android 8.0 and 17 |
| Real phones, real impacts, heat, Bluetooth, a real car | **not yet** |

The gap between two clips was 0.1-1 s on the emulators (longer when the PC was overloaded). If the phone loses power or the app is killed, the clip being recorded at that moment is lost.

## Security and privacy

- No internet permission; a library cannot add one (removed in the manifest). No cloud backup of settings.
- Read-only towards the car: services 01, 02, 03, 07, 09, 0A, 19 (01/02/0A), 21, 22 only.
- Emergency button only opens the dialler, with a number from a fixed list.
- The log contains no positions or car data. Videos, `.srt`, `.gpx` and `incident.txt` do contain positions unless GPS is turned off, and other apps with file access can read them.
- The emergency and impact features are aids, not safety systems. Do not operate the phone while driving.
