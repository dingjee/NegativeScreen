# NegativeScreen Project Rules

## Project Overview

NegativeScreen is a Windows application that applies real-time color effects to the screen using the Windows Magnification API. It can invert colors, adjust brightness/contrast, and apply various color transformations to help reduce eye strain or assist users with visual impairments.

## Build Commands

### Build Main Project
```powershell
C:\Windows\Microsoft.NET\Framework64\v4.0.30319\MSBuild.exe d:\Projects\NegativeScreen\NegativeScreen.sln /p:Configuration=Release /p:Platform=x64
```

### Build Output
- Release: `d:\Projects\NegativeScreen\NegativeScreen\bin\x64\Release\NegativeScreen.exe`

### Run Tests
```powershell
C:\Windows\Microsoft.NET\Framework64\v4.0.30319\MSBuild.exe d:\Projects\NegativeScreen\test\TestRunner.csproj /p:Configuration=Release /p:Platform=x64
& "d:\Projects\NegativeScreen\test\bin\x64\Release\TestRunner.exe"
```

## Technology Stack

- **Framework**: .NET Framework 4.5
- **Language**: C# (Windows Forms)
- **Platform**: Windows x64
- **Key API**: Windows Magnification API (Magnification.dll)

## Architecture

### Core Components

1. **OverlayManager.cs** - Main application form and control logic
   - Manages the system tray icon and context menu
   - Controls the main loop for applying effects
   - Handles monitor selection and per-monitor settings

2. **MagnifierWindow.cs** - Wrapper for magnifier API windows
   - Creates and manages magnifier windows per monitor
   - Applies color matrices for effects
   - Handles window positioning and visibility

3. **MonitorManager.cs** - Multi-monitor management
   - Enumerates display monitors
   - Tracks monitor configurations

4. **Configuration.cs** - Application settings
   - Loads/saves user preferences
   - Manages color effect presets

5. **NativeMethods.cs** - Windows API interop
   - Magnification API P/Invoke declarations
   - Window management functions

### Key State Variables

- `mainLoopPaused` - Controls whether effects are active
- `magInitialized` - Tracks Magnification API initialization state
- `usePerMonitorMode` - Whether using per-monitor or all-monitors mode
- `enabledMonitors` - Set of monitor IDs with active effects
- `currentMatrix` - Current color transformation matrix

### Control Flow

1. Application starts → `OverlayManager` created
2. `InitializeControlLoop()` starts background thread
3. Control loop:
   - If `mainLoopPaused`: wait in pause loop
   - When activated: `MagInitialize()` → Create windows → Apply effects
   - When deactivated: Disable windows → `MagUninitialize()`

## Important Patterns

### Magnification API Usage

```csharp
// Must initialize before creating windows
NativeMethods.MagInitialize();

// Create magnifier window
var window = new MagnifierWindow(monitor);
window.SetColorEffect(matrix);
window.Enable();

// Must uninitialize when done
NativeMethods.MagUninitialize();
```

### Thread Safety

- UI operations must use `Invoke()` for cross-thread calls
- `magInitLock` protects Magnification API initialization state
- Color effect changes use `invokeColorEffectLock`

### Menu State Synchronization

- `UpdateMonitorMenuChecks()` syncs menu with actual state
- Must check `!mainLoopPaused` for correct active state display
- `BuildMonitorMenu()` creates menu items with initial state

## Configuration

### Key Settings

- `ActiveOnStartup` - Whether to apply effects on launch (default: false)
- `MainLoopRefreshTime` - Refresh interval in milliseconds
- `ColorEffects` - List of available color effect presets

### Configuration File

Location: `%APPDATA%\NegativeScreen\NegativeScreen.config`

## Common Issues and Solutions

### Error 1407 (Cannot Find Window Class)

**Cause**: Magnification API not initialized before creating windows

**Solution**: Ensure `MagInitialize()` is called before `MagnifierWindow.Create()`

### Menu Checkmarks Out of Sync

**Cause**: Menu state not considering `mainLoopPaused`

**Solution**: Check `isActive = !mainLoopPaused` in `UpdateMonitorMenuChecks()`

### InvalidCastException with ToolStripSeparator

**Cause**: Iterating menu items without type checking

**Solution**: Use `as ToolStripMenuItem` and null check

## File Structure

```
NegativeScreen/
├── NegativeScreen/
│   ├── OverlayManager.cs      # Main form and control logic
│   ├── OverlayManager.Designer.cs  # Form designer
│   ├── MagnifierWindow.cs     # Magnifier window wrapper
│   ├── MonitorManager.cs      # Monitor enumeration
│   ├── Configuration.cs       # Settings management
│   ├── ConfigurationParser.cs # Config file parsing
│   ├── NativeMethods.cs       # Windows API interop
│   ├── NativeStructures.cs    # Native structures
│   ├── BuiltinMatrices.cs     # Color matrix presets
│   ├── Api.cs                 # API helpers
│   ├── Program.cs             # Entry point
│   └── bin/x64/Release/       # Build output
├── test/
│   ├── TestRunner.cs          # Test program
│   ├── TestRunner.csproj      # Test project
│   └── run_tests.bat          # Test runner script
└── .trae/rules/
    └── project_rules.md       # This file
```

## Color Matrices

The application uses 5x5 color transformation matrices for effects:

```csharp
// Example: Inversion matrix
float[,] inversionMatrix = new float[,] {
    { -1,  0,  0,  0,  0 },
    {  0, -1,  0,  0,  0 },
    {  0,  0, -1,  0,  0 },
    {  0,  0,  0,  1,  0 },
    {  1,  1,  1,  0,  1 }
};
```

See `BuiltinMatrices.cs` for all available presets.

## Development Notes

1. Always run tests before notifying user of changes
2. The Magnification API requires Windows Vista or later
3. Effects are applied via transparent windows overlaying the screen
4. The main loop runs on a separate STA thread
5. Hotkeys are registered globally via `RegisterHotKey`
