# LidGuard

A lightweight, native Windows 11 system tray utility with a Fluent Design / Mica WPF interface that automatically prevents your laptop from sleeping when docked to an external monitor and switches Windows Modern Standby power modes.

## Features

- **Dock-Aware Lid Close Automation**:
  - **External Display Connected**: Automatically sets Plugged-In Lid Close Action to **Do Nothing** so you can close your laptop lid in clamshell mode without interrupting your external monitor.
  - **Internal Screen Only**: Automatically restores Plugged-In Lid Close Action to **Sleep** when you undock so your laptop sleeps normally in your bag.
- **Automatic High Power Mode (`powrprof.dll` Modern Standby Overlay)**:
  - Switches Windows 11 Power Mode to **Best Performance** when plugged into AC power and connected to an external display.
  - Reverts to **Balanced** when undocked or running on battery.
- **Native Windows 11 Fluent UI**:
  - Real DWM Mica backdrop (`DWMWA_SYSTEMBACKDROP_TYPE = 2`) with automatic Dark/Light mode, Windows System Accent Color integration, and crisp ClearType typography.
  - Customizable system tray right-click menu items.
- **Separated Standalone Installer (`LidGuardInstaller.cs`)**:
  - Fetches the latest files from GitHub (`SStarlight9614/Lid-Guard`) and installs to `C:\Program Files\LidGuard` (for 64-bit) or `C:\Program Files (x86)\LidGuard` (for 32-bit).
  - Includes one-click toggles for **Add to Start Menu** (checked by default) and **Create Desktop Shortcut**, then automatically launches `LidGuard.exe` upon completion.
- **GitHub Auto-Updater**:
  - Built-in update checker in Settings (`Check Updates`) that queries GitHub Releases and installs updates in-place.

## Building from Source

No external SDK installation is required—LidGuard compiles using the built-in Windows .NET Framework 4.8 64-bit compiler (`csc.exe`):

```powershell
powershell -ExecutionPolicy Bypass -File .\build.ps1
```
