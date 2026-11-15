<img src="docs/logo.png" width="72" align="left" alt="">

# WinMonitor

A small floating hardware monitor for Windows, with **wireless device batteries** built in.
Free and open source (MIT). Built with WinUI 3. Portable: unzip and run.

## Features

- **Floating panel** with CPU, GPU, memory, motherboard, disks, network, daily traffic, laptop battery and a small dashboard (time, uptime, host name, IP)
- **Wireless device batteries** (mouse, keyboard, headset, controller, Bluetooth), read straight from the devices:
  - Razer (receiver and wired)
  - Logitech (HID++, e.g. Lightspeed)
  - PlayStation DualShock 4 / DualSense (USB and Bluetooth)
  - Xbox controllers (XInput, approximate levels)
  - Bluetooth devices that report a battery to Windows
  - Low battery alert, red and yellow rows near the limit, ⚡ while charging
- **Look**: Windows 11 light and dark themes (follow Windows by default), your own themes with a live-preview editor, width, scale, opacity, per-row bars, renamable rows and groups, custom units
- **Behaviour**: always on top, click-through, keep on screen, remembers its place per monitor
- **Alerts**: temperature and battery notifications
- **History**: temperature, load, power and clock for up to 7 days, with CSV export
- **Hardware info**: every sensor with value, min and max

## Changes from LiteMonitor

WinMonitor started as a port of LiteMonitor. The sensor code carries over; the interface is new.

**Added**
- Rebuilt with WinUI 3: Windows 11 style settings, history, hardware info and theme editor windows
- Wireless device batteries built in (from Halo Battery): Razer, Logitech, PlayStation, Xbox and Bluetooth, with a low battery alert and coloured rows
- Windows 11 light and dark themes that follow the Windows setting
- A separate temperature alert limit for disks
- Settings apply straight away (no OK / Cancel)
- Optional downloads come only from the official release and are checked against a pinned SHA-256
- Portable, self-contained build: no installer, no .NET install needed
- Lower memory use (about 14 MB in Task Manager after idling)
- `--open` shortcuts for the windows

**Removed**
- Taskbar mode and the horizontal bar layout
- Auto-hide at screen edges
- FPS (PresentMon)
- Web view, plugins, automatic updates and languages other than English
- The 10 bundled themes (the two Windows 11 themes remain; make your own in the theme editor)
- Extras: clean memory, prevent sleep, turn off display, scheduled shutdown, restart Explorer, refresh icons, clean temp files

**Changed**
- Always on top is off by default
- Double-click opens Settings (or Task Manager, or nothing)
- Keep on screen now works while dragging, on the monitor under the cursor

## Install

1. Download `WinMonitor-portable-x64.zip` from [Releases](https://github.com/znbaboy/WinMonitor/releases).
2. Unzip anywhere and run `WinMonitor.exe`.

WinMonitor asks for administrator rights: Windows needs them for sensor access. Settings and history are kept next to the exe. To update, close WinMonitor and unzip the new version over the old folder.

**Optional:** the **PawnIO driver** for CPU temperature, clock and power ([namazso/PawnIO.Setup](https://github.com/namazso/PawnIO.Setup)). WinMonitor asks before downloading it, takes it only from the official release and checks it against a pinned SHA-256.

## Use

- **Right-click** the panel (or the tray icon) for the menu
- **Drag** to move it; **double-click** opens Settings (can be changed)
- **Settings → Monitor Items**: show, hide, rename, reorder rows, set units and switch bars on or off
- **Settings → Panel → Wireless devices**: how often batteries are read, PlayStation over Bluetooth, and a device report for troubleshooting

Shortcuts: `WinMonitor.exe --open settings`, `--open settings:Items`, `--open history`, `--open hardware`, `--open themes`.

## Build

Windows, .NET 8 SDK and Visual Studio Build Tools (MSBuild):

```powershell
msbuild WinMonitor.csproj /restore /t:Publish /p:Configuration=Release /p:Platform=x64 /p:RuntimeIdentifier=win-x64 /p:PublishDir=publish\WinMonitor\
```

GitHub Actions builds every push, runs launch tests on Windows and takes screenshots (artifacts). Push a `v*` tag to publish a release.

## Credits

WinMonitor is based on [LiteMonitor](https://github.com/Diorser/LiteMonitor) by Diorser (MIT), with the battery readers of [Halo Battery](https://github.com/HeyOkay/HaloBattery) (MIT), sensors from [LibreHardwareMonitor](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor) (MPL-2.0) and a window technique from [WinUIEx](https://github.com/dotMorten/WinUIEx) (MIT). See [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt).
