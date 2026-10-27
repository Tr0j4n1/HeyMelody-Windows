# MelodyDesk

A Windows desktop controller for OPPO / OnePlus / realme earbuds, rebuilt from the HeyMelody 116.9.0 Android app.
It talks to the earbuds directly over Bluetooth Classic (RFCOMM). There is no account, cloud or phone involved.

Tested against a **OnePlus Nord Buds 3 Pro** (firmware 111.155.155).

## Use

1. Pair the earbuds with Windows (Settings → Bluetooth & devices).
2. Run `MelodyDesk.exe` (or build it, see below) and click **Connect** next to the earbuds' name. After the first successful connection, the app connects on its own at start-up.

The window is laid out like Windows 11 Settings: a navigation pane on the left (Device, Earbud controls, About) and the page on the right. The controls themselves keep the HeyMelody phone app's look.

**Device**
- **Header card**: the earbuds picture, name, connection status, and a battery for each earbud (and the case while it reports a level). The ⋮ button picks another paired device, connects or disconnects, opens the raw traffic log, or exits.
- **Noise control**: Noise cancellation, Off and Transparency, plus the Personalised noise cancellation switch.
- **Device management**: Dual connection ›, which opens its switch and the devices the buds remember, with the ones connected now marked.
- **Audio effects**: Sound Master EQ ›, which opens:
  - the Balanced, Serenade and Bass presets
  - BassWave™ with its level slider
  - your custom EQs (Custom1, Custom2) for selection
- **Gaming features**: Game mode.
- **Other features**: Find my earbuds ›, with a play/stop button.
- **Earbud settings**: More settings › (Auto play/pause).
- **About Earbuds**: the firmware version, in the phone's format (for example `155.155.101`).

Pages opened from the Device page show a breadcrumb (for example **Device › Sound Master EQ**); click **Device** or the back arrow to return.

**Earbud controls**
- A Left/Right picker, then the gestures under the phone's names (single tap, double-tap, triple-tap, long touch and hold, touch and hold).
- Click a gesture to change its function: Play/Pause, Previous, Next, Game mode or None. Touch and hold offers Noise cancellation / Transparency or None.
- "When on a call" shows the fixed call controls.

**About**: app version and what the app is built from, plus **Appearance**:
- **Use system setting** (default) follows Windows' app mode (Settings → Personalisation → Colours) and switches live when you change it.
- **Light** or **Dark** fixes the theme. The choice is saved in `%APPDATA%\MelodyDesk\theme.txt`.

Dark mode covers the window and its title bar, the menus, the tray panel and the tray menu. The dark palette uses HeyMelody's own night colours; the window surfaces follow Windows 11. `--selftest` checks that text in both themes stays at WCAG AA contrast (4.5:1).

**What's shown for each model**

What appears for the **Nord Buds 3 Pro** is copied from the phone app. That covers which rows appear, the three noise buttons, and the EQ preset names; the preset numbers (3, 4 and 5) were read from the buds while each was picked on the phone. The buds also answer for Vocal enhancement, Hi-Res, Spatial Audio, Camera control and Game EQ. The phone hides those for this model, so MelodyDesk does too.

Other models fall back to a generic view that shows every switch the buds report, all four noise modes, and EQ presets by number.

The app icon, colours, switch and button styles, vector icons and earbuds render are taken from the APK. `Assets/make-icon.ps1` rebuilds `app.png` and `app.ico` from HeyMelody's adaptive launcher icon layers (`mipmap-xxxhdpi/heymelody_app_icon_launcher_*`).

### Notification area (tray)

MelodyDesk keeps running in the notification area. On Windows 11 new icons start under **Show hidden icons** (`^`); you can drag the icon onto the taskbar to keep it in view.

- **Hover**: the tooltip shows the battery levels.
- **Icon**: the HeyMelody app icon.
- **Left-click**: a small panel opens above the tray, with the battery strip and the noise buttons. **Open MelodyDesk** shows the full window.
- **Right-click**: a menu with:
  - the noise modes
  - Open MelodyDesk
  - Connect / Disconnect
  - **Start with Windows** (off by default; it adds `MelodyDesk.exe --tray` to your user's Run key)
  - **Exit**
- **Closing the window** only hides it. Use **Exit** to quit.
- **Running `MelodyDesk.exe` again** brings the existing window back instead of starting a second copy.
- **Auto-reconnect**: the buds drop the link when they go into a closed case. MelodyDesk reconnects within about 15 seconds of Windows seeing them again, unless you chose Disconnect.

### Noise control and the charging case

The buds change their own noise mode when they go into the case. With one bud in the case they may switch to Adaptive, with both in they switch to Off, and when taken out they often come back in Transparency. MelodyDesk handles this as follows:

- **Your mode is what you last picked**, in the app or by pinch-and-hold on the buds, while both buds were in your ears. The buttons keep showing it while a bud is out.
- **While a bud is out**, a note under the buttons says which mode the buds switched to.
- **With no bud in your ears**, changes are blocked with HeyMelody's own "Wear your earbuds to use Noise control".
- **When both buds are back in your ears**, MelodyDesk sets your mode again.

Your mode is remembered for as long as MelodyDesk is running. It is not saved between restarts.

## Build

Run `build.cmd`. It compiles with the C# compiler and WPF that ship with Windows (.NET Framework 4.x). No SDK or NuGet packages are needed.
- The layout lives in `MainWindow.xaml` and the shared styles in `Styles.xaml`. Both are embedded in the exe and loaded at runtime, along with the images in `Assets`. `Assets/app.ico` is also the exe's icon.
- After building, the script runs `MelodyDesk.exe --selftest`. It checks the frame codec against bytes captured from real earbuds, and replays the captured case sequence through the noise-mode memory.

If the UI hits an unexpected error, the app keeps running and writes the details to `%APPDATA%\MelodyDesk\error.log`.

## Protocol (reverse-engineered)

**Transport**
- RFCOMM to service UUID `0000079A-D102-11E1-9B23-00025B00A5A5`, with fallback `00001107-D102-11E1-9B23-00025B00A5A5`.
- Windows resolves the RFCOMM channel through SDP.

**Link frame** (`OPOv1Wrapper`):
```
AA | varint(len) | fsn-control | 00 | packet
```
- `len` covers the two control bytes plus the packet.
- In the control byte, `&3`: `0` = single frame, `1`/`2`/`3` = first/middle/last slice. Slices carry one extra sequence byte after the `00`.

**Packet** (`Packet`, `TLVDataProcessor`):
```
cmd (LE16) | seq (u8) | len (LE16) | payload
```
- Responses set `cmd | 0x8000` and echo `seq`.
- The first byte of a response payload is a status (`0` = OK).

| Cmd | Direction | Payload | Meaning |
|---|---|---|---|
| `0100` | get | – | capability bitmap → supported commands (table `R6.a`) |
| `0105` | get | – | firmware: CSV of `device,type,version` triples |
| `0106` | get | – | battery: `[n, (device, level\|0x80 charging)…]`; device 1=L 2=R 3=case |
| `0108` | get | – | gestures: `[n, (device, button, action, function)…]` |
| `0109` | get | – | ear status: `[n, (device, flags)]`; bit0 clear = in case, bit1 = in ear |
| `010C` | get | `01 01` | current noise mode: `[01, 01, bitmask]` |
| `010D` | get | `[n, ids…]` | feature switches → `[n, (id, on)…]`. **Keep `n` ≤ 20: larger queries make the buds drop the link.** |
| `010F` | get | – | EQ preset index (a custom EQ's id while one is selected) |
| `0112` | get | – | remembered devices: `[n, (address(6), len, state, flags, nameLen, name)…]`; state 2 = connected, flags bit0 = the asking host |
| `0122` | get | – | custom EQs: `[n, (selected, min, max, id, nameLen, name, bands, (freq LE16, dB)…)…]` |
| `0124` | get | – | BassWave level: `[min, max, value]`, signed (−10…10) |
| `0200` / `0205` | get / set | `[n, events…]` | event capability, then register for those events |
| `0204` | push | `[type, …]` | events: 1 battery, 2 ear status, 3 noise mode, 5 game mode, 6 remembered devices, F1 gesture used |
| `0400` | set | `[1\|0]` | find earbuds: start/stop the sound |
| `0401` | set | `[n, (device, button, action, function)…]` | gestures. Actions: 1 single tap, 2 double-tap, 3 triple-tap, 6 long touch and hold, 4 touch and hold. Functions: 0 None, 1 Play/Pause, 5 Previous, 6 Next, 8 noise control, 17 Game mode |
| `0403` | set | `[id, 1\|0]` | feature switch |
| `0404` | set | `[01, 01, bitmask]` | noise mode |
| `0406` | set | `[index]` | EQ preset |
| `0418` | set | `[action, custom EQ body]` | custom EQ: **1 adds a new one**, 2 selects, 3 deletes |
| `041B` | set | `[min, max, value]` | BassWave level |
| `0500` | push | – | time request; HeyMelody replies `8500 [00, unix seconds LE32]` |
| `0503` / `0504` | push | `[n, (id, on)…]` / `[index]` | device-side feature and EQ changes |

Command groups:
- `0x01xx` get
- `0x02xx` events
- `0x03xx` firmware update
- `0x04xx` set
- `0x05xx` device-initiated requests

**Values specific to the Nord Buds 3 Pro**, found by setting each value and reading it back:
- **Noise mask bits** (identified by ear): `0` Off, `2` Transparency, `4` Noise cancellation, `5` Adaptive. Bits `1` and `3` are accepted but become `4` and `2`.
- **EQ presets:** indexes `0`–`6` are accepted. The phone offers `3` Balanced (Default), `4` Serenade and `5` Bass. Custom EQs use ids from `7` up.
- **Feature switches the phone shows:** `4` Auto play/pause, `6` Game mode, `12` Personalised noise cancellation, `17` Dual connection, `29` BassWave.

HeyMelody gets the display names for noise modes, EQ presets and gesture functions from OPPO's per-model cloud config, not from the APK. The names above come from the phone app.

## Not included

- Firmware updates (`0x03xx`)
- Creating, renaming or editing custom EQs (selecting existing ones works)
- Earbud fit test and the Personalised noise cancellation ear scan (the switch works)
- Themes, hearing tests and spatial-audio calibration
- Account and cloud features
