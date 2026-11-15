# ⚡ WinMonitor

A small always-on-top hardware monitor for Windows, with **wireless device batteries** built in.
Free and open source (MIT). Built with WinUI 3. Portable: unzip and run.

![WinMonitor](docs/screenshot.png)

## Features

- **Floating panel** with CPU, GPU, memory, FPS, motherboard, disks, network, daily traffic, laptop battery and a small dashboard (time, uptime, host name, IP)
- **Wireless device batteries** (mouse, keyboard, headset, controller, Bluetooth), read straight from the devices:
  - Razer (receiver and wired)
  - Logitech (HID++, e.g. Lightspeed)
  - PlayStation DualShock 4 / DualSense (USB and Bluetooth)
  - Xbox controllers (XInput, approximate levels)
  - Bluetooth devices that report a battery to Windows
  - Low battery alert, red and yellow rows near the limit, ⚡ while charging
- **Two layouts**: a panel, or a compact bar
- **Look**: 10 themes and a theme editor with live preview, width, scale, opacity, per-row bars, renamable rows and groups, custom units
- **Behaviour**: always on top, click-through, auto-hide at screen edges, keep on screen, remembers its place per monitor
- **Alerts**: temperature and battery notifications
- **History**: temperature, load, power, clock and FPS for up to 7 days, with CSV export
- **Hardware info**: every sensor with value, min and max
- **Extras**: clean memory, prevent sleep, turn off the display, scheduled shutdown, restart Explorer, clean temp files

## Install

1. Download `WinMonitor-portable-x64.zip` from [Releases](https://github.com/znbaboy/WinMonitor/releases).
2. Unzip anywhere and run `WinMonitor.exe`.

WinMonitor asks for administrator rights: Windows needs them for sensor access. Settings and history are kept next to the exe. To update, close WinMonitor and unzip the new version over the old folder.

**Optional components** (asked before downloading; official releases only, checked against a pinned SHA-256):
- **PawnIO driver** for CPU temperature, clock and power: [namazso/PawnIO.Setup](https://github.com/namazso/PawnIO.Setup)
- **PresentMon** for FPS: [GameTechDev/PresentMon](https://github.com/GameTechDev/PresentMon) (MIT)

## Use

- **Right-click** the panel (or the tray icon) for the menu
- **Drag** to move it; **double-click** switches between the panel and the bar (this action can be changed in Settings)
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
