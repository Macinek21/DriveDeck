# Installing DriveDeck on a Windows 10 Mobile phone

DriveDeck is not in the Microsoft Store, so it is installed ("sideloaded") from a PC using the phone's developer mode. This takes about 10 minutes the first time.

## What you need

| | |
|---|---|
| **Phone** | Windows 10 Mobile, version **1607 (OS build 10.0.14393)** or newer. Check on the phone: *Settings › System › About › OS build*. Developed and tested on a Nokia Lumia 930. |
| **PC** | Windows 10 or 11, connected to the **same Wi-Fi network** as the phone. |
| **Download** | `DriveDeck_<version>_ARM.zip` from the project's [Releases](../../releases) page. |
| **For Method A only** | Microsoft's *Windows 10 SDK, version 10.0.19041* (free), which contains the `WinAppDeployCmd` tool. Not needed for Method B. |
| **For OBD data** | An ELM327 adapter with **classic Bluetooth** (the kind you pair with a PIN). Bluetooth LE and Wi-Fi adapters are not supported. |

## Step 1 – Unzip the download

Unzip `DriveDeck_<version>_ARM.zip` into a short folder, for example `C:\DriveDeck`. It contains:

```
DriveDeck.App_<version>_ARM.appx      the app
DriveDeck.App_<version>_ARM.cer       its (self-signed) certificate
Dependencies\arm\*.appx               3 Microsoft runtime packages the app needs
install-to-phone.ps1                  installer script for Method A
```

> Avoid deep folders: Microsoft's deployment tool fails with error `0x800700CE` on long paths. (The script works around it, but a short folder is simplest.)

## Step 2 – Prepare the phone

1. Open *Settings › Update & security › For developers*.
2. Select **Developer mode** and confirm the warning.
3. Turn on **Device discovery**.
4. Make sure the phone is on the same Wi-Fi as the PC.

## Step 3 – Install

### Method A – installer script (tested)

1. Install the Windows 10 SDK 10.0.19041 on the PC, if you do not have it.
2. In the unzipped folder, right-click empty space with **Shift** held › *Open PowerShell window here*, then run:
   ```powershell
   powershell -ExecutionPolicy Bypass -File .\install-to-phone.ps1
   ```
3. The script lists phones it finds (`IP Address … Win-Mobile-10`). Type the phone's IP address.
4. On the phone, under *Device discovery*, tap **Pair**. Type the code it shows into the PowerShell window. (Next time you can just press Enter – the PC stays paired.)
5. Wait for `Done.` The app appears in the phone's app list as **DriveDeck**.

### Method B – Device Portal in a web browser (no SDK needed)

> This is Microsoft's standard sideloading method; the author has tested Method A only. Please report how it went.

1. On the phone, under *For developers*, turn on **Device Portal** (leave *Authentication* on). The phone shows an address such as `https://192.168.1.23`.
2. Open that address in a browser on the PC. Accept the certificate warning (the phone uses its own self-signed certificate).
3. When the browser asks for a code, tap **Pair** on the phone and type the code shown.
4. Go to **Apps** (App manager) › *Install app*:
   - **App package**: choose `DriveDeck.App_<version>_ARM.appx`
   - **Certificate**: choose `DriveDeck.App_<version>_ARM.cer`
   - **Dependency**: add each of the three files in `Dependencies\arm`
5. Click **Go** / **Install** and wait until it finishes.

## Step 4 – First start

1. Open **DriveDeck** and turn the phone to landscape.
2. Allow **camera**, **location** and access to **Videos** when asked (and the microphone, if you later enable sound recording). You can change this later in *Settings › Privacy*.
3. The camera tile should show **● REC** with a running timer. Clips are saved every minute to `Videos\DriveDeck\Loop`.
4. Open the menu (☰, bottom right) › **Settings** to set resolution, storage limit, speed stamp, impact sensitivity and more, and › **Edit dashboard layout** to arrange the tiles.

DriveDeck must stay on screen while recording – Windows 10 Mobile pauses apps in the background. It keeps the screen on by itself; use a car charger.

## Step 5 – Connect the car (optional)

1. Plug the ELM327 into the car's OBD-II port (usually under the dashboard, driver's side) and switch the ignition on.
2. On the phone: *Settings › Devices › Bluetooth*, pair the adapter (often named `OBDII` or `ELM327`, PIN `1234` or `0000`).
3. In DriveDeck: *Settings › OBD-II › Adapter*, choose it, pick the protocol (*CAN 11-bit 500 kbit* for most cars from 2008 on, or *Automatic*), and **Save**.
4. The status tile shows `OBD: ELM327 v…, protocol …` once connected; values appear in the tiles.

**Unplug the adapter when you leave the car** – cheap ELM327 clones accept any Bluetooth connection with the default PIN.

## Finding your recordings

On the phone they are in the **Photos** app (videos) and in DriveDeck › ☰ › **Recordings**. On a PC, connect the phone by USB and open *This PC › (phone) › Phone › Videos › DriveDeck*:

| Folder / file | Contents |
|---|---|
| `Loop\*.mp4` | Rolling clips; the oldest are overwritten when the storage limit is reached |
| `Loop\*.srt`, `*.gpx` | Speed/position subtitles and GPS track for each clip (if enabled) |
| `Events\<time>_impact` / `_manual` | Saved incidents with `incident.txt` – never overwritten |
| `DriveDeck-log.txt` | Diagnostic log (app events and errors; no positions or car data) |

If files seem to be missing over USB, unplug and reconnect the phone – Windows caches the phone's file list.

## Updating

Download the newer release and install it the same way (Method A or B). Settings and recordings are kept.

## Uninstalling

On the phone: app list › press and hold **DriveDeck** › **Uninstall**. Recordings in `Videos\DriveDeck` are kept; delete that folder yourself if you no longer need them. You can turn *Developer mode* off again afterwards.

## Troubleshooting

| Problem | Solution |
|---|---|
| Script finds no phone | Same Wi-Fi on both? *Device discovery* on? Type the IP manually (shown in *For developers* when Device Portal is on). Check that the router does not isolate Wi-Fi clients (guest networks often do). |
| `0x800700CE` (path too long) | Move the unzipped folder to a short path such as `C:\DriveDeck`. |
| Pairing code rejected | Codes expire quickly – tap **Pair** again and use the new code. |
| "Install failed" for an existing installation | The script retries as an update automatically. A release with the same or lower version number than the installed one cannot be installed over it – uninstall first. |
| Camera tile says "not recording" | Check the notification badge (top right) and `Videos\DriveDeck\DriveDeck-log.txt`. Make sure camera and Videos access are allowed in *Settings › Privacy*. |
| "HOT – paused" | The phone overheated; it resumes when cool. Use 720p and keep the phone out of direct sun. |
| No OBD values | Ignition on? Adapter paired and selected? Try protocol *Automatic*. Values the car does not support show `n/a`. |

Still stuck? Open an issue and attach `DriveDeck-log.txt` (check it first – it contains no locations, but read it before sharing anything).
