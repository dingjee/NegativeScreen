# NegativeScreen - Project Guide

## Build

```powershell
C:\Windows\Microsoft.NET\Framework64\v4.0.30319\MSBuild.exe d:\Projects\NegativeScreen\NegativeScreen.sln /p:Configuration=Release /p:Platform=x64
```

## Output

- **Executable**: `D:\Projects\NegativeScreen\NegativeScreen\bin\x64\Release\NegativeScreen.exe`
- **Configuration file**: `D:\Projects\NegativeScreen\NegativeScreen\bin\x64\Release\negativescreen.conf`

## Configuration File Locations (priority order)

1. `%APPDATA%\NegativeScreen\negativescreen.conf` (AppData)
2. Working directory `negativescreen.conf` (next to .exe)
3. Embedded default configuration (fallback)

## Key Configuration Values

| Key | Type | Default | Description |
|-----|------|---------|-------------|
| `SavedBrightness` | float | 0.0 | Saved brightness value (auto-updated by app) |
| `SavedContrast` | float | 1.0 | Saved contrast value (auto-updated by app) |
| `MagnifierScaleX` | float | 0.75 | Manual DPI scale fix for non-primary monitors |
| `MagnifierScaleY` | float | 0.75 | Manual DPI scale fix for non-primary monitors |
| `UseManualMagnifierWorkarounds` | bool | false | Enable manual multi-monitor DPI workarounds |
| `InitialColorEffect` | string | "Smart Inversion" | Color effect applied on startup |
| `ActiveOnStartup` | bool | true | Whether the effect is active on launch |
| `Toggle` | hotkey | win+alt+N | Hotkey to toggle the effect |
| `Exit` | hotkey | win+alt+H | Hotkey to exit |

## Architecture Notes

- **Framework**: .NET Framework 4.5, WinForms
- **Language**: C#
- **Core classes**:
  - `Configuration.cs` — Config parsing, `SaveValue()` for persisting values
  - `OverlayManager.cs` — Main form, tray icon, brightness/contrast sliders, per-monitor magnifier window management
  - `MagnifierWindow.cs` — Per-monitor overlay using Windows Magnification API
  - `BuiltinMatrices.cs` — Color transformation matrices, brightness/contrast application
  - `MonitorManager.cs` — Multi-monitor detection and management
  - `ConfigurationParser.cs` — Generic key=value parser with attribute-based mapping
