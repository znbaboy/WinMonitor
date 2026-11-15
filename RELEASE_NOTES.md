# WinMonitor 1.0.0

First release. A small floating hardware monitor for Windows with wireless device batteries built in.

**Install:** download `WinMonitor-portable-x64.zip`, unzip anywhere and run `WinMonitor.exe` (it asks for administrator rights, which Windows needs for sensor access). Nothing is installed; settings stay in the folder.

## Changes from LiteMonitor

WinMonitor started as a port of LiteMonitor. The sensor code carries over; the interface is new.

**Added**
- Rebuilt in WinUI 3: Windows 11 style panel, settings, history, hardware info and theme editor windows
- Wireless device batteries built in (from Halo Battery): Razer, Logitech, PlayStation, Xbox and Bluetooth, with emoji row titles, ⚡ while charging, red and yellow rows near the limit and a low battery alert
- Built-in **Dark** and **Light** Windows 11 themes, plus **Use system theme** to follow Windows (built-ins are locked; duplicate one to customise it)
- Monitor Items page redone: collapsible categories, a switch per category (off greys out its items and hides it from the panel), drag handles to reorder rows and categories, name and unit edited right in the row
- Category on/off and per-item show/hide in the tray menu
- History also records hidden items whose hardware is already being read (no extra sensor polling)
- Optional min–max shading on the history graph (off by default)
- Separate temperature alert limit for disks
- Settings apply straight away (no OK / Cancel)
- Optional PawnIO driver download only from the official release, checked against a pinned SHA-256
- Portable, self-contained build: no installer, no .NET install needed
- Much lower memory use (about 14 MB after idling)
- `--open settings|history|hardware|themes` command-line shortcuts
- New Windows 11 style app icon

**Removed**
- Taskbar mode and the horizontal bar layout
- Auto-hide at screen edges
- FPS counter (PresentMon)
- Web view, plugins, automatic updates and languages other than English
- The 10 bundled themes (make your own in the theme editor)
- Extras: clean memory, prevent sleep, turn off display, scheduled shutdown, restart Explorer, refresh icons, clean temp files

**Changed**
- Always on top is off by default
- Double-click opens Settings (or Task Manager, or nothing)
- Keep on screen works while dragging, on the monitor under the cursor
- No white frame or outline when moving the panel between monitors
