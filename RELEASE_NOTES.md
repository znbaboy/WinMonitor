# WinMonitor 1.0.1

A small floating hardware monitor for Windows 11, with wireless device batteries built in.

**Install:** unzip `WinMonitor-portable-x64.zip` anywhere and run `WinMonitor.exe`. 1.0.0 users: download this once; later updates install from Settings > About.

## New in 1.0.1

- Update check with one-click install, and PawnIO driver install / update (Settings > About)
- Laptop battery and wireless devices in one Battery category; laptop battery rows hide themselves on PCs without a battery
- Even spacing between rows with and without a bar
- Monochrome emoji option, and a switch to turn off clock speed colours
- Plain device names, plain-language low battery notification
- Number boxes without arrow buttons; units always as reported
- Start with Windows runs at normal priority and follows the exe if you move it
- Windows 11 style dialogs; download is now just `WinMonitor.exe` (settings.json is created on first start)

## Changes from LiteMonitor

**Added**
- Windows 11 look (WinUI 3) across the panel and every window
- Wireless device batteries (from Halo Battery): Razer, Logitech, PlayStation, Xbox and Bluetooth, with a low battery alert
- Built-in Dark and Light themes, or follow Windows
- Simpler Monitor Items page: collapsible categories, drag to reorder, rename in place, switch whole categories off
- One Battery category for laptop and wireless devices; laptop rows hidden on PCs without a battery
- History keeps hidden items that are already being read; optional min–max shading
- Separate temperature alert limit for disks
- Portable and self-contained: no installer, no .NET needed
- Lighter on memory (about 14 MB)
- Update check with one-click install, and PawnIO driver install / update, in Settings > About

**Removed**
- Taskbar mode and horizontal bar layout
- Auto-hide at screen edges
- FPS counter (PresentMon)
- Web view, plugins and non-English languages
- The 10 bundled themes (make your own in the theme editor)
- Extras: clean memory, prevent sleep, turn off display, scheduled shutdown, restart Explorer, refresh icons, clean temp files

**Changed**
- Always on top is off by default
- Double-click opens Settings
- Settings apply instantly (no OK / Cancel)
