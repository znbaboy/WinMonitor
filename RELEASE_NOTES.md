# WinMonitor 1.0.0

First release. A small floating hardware monitor for Windows with wireless device batteries built in.

**Install:** download `WinMonitor-portable-x64.zip`, unzip anywhere and run `WinMonitor.exe` (it asks for administrator rights, which Windows needs for sensor access). Nothing is installed; settings stay in the folder.

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
