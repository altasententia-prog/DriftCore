# DriftCore

Real-time controller drift correction for Windows.  
Reads XInput → DriftCore engine → ViGEm virtual Xbox controller.

---

## Prerequisites

Before building or running, install these two free drivers:

### 1. ViGEmBus (required)
Creates the virtual Xbox controller games receive input from.

**Download:** https://github.com/ViGEm/ViGEmBus/releases  
Run the installer. Requires a reboot.

### 2. HidHide (optional but recommended)
Hides the physical controller from games so only the corrected virtual 
controller is visible. Without it, games may receive both raw and corrected 
input simultaneously.

**Download:** https://github.com/ViGEm/HidHide/releases  
After install, open HidHide Configuration Client and add `DriftCore.exe` 
as a whitelist application.

---

## Building

Requirements: Visual Studio 2022+ with .NET 8 Desktop workload, or the .NET 8 SDK.

```
# Restore NuGet packages and build
dotnet restore DriftCore/DriftCore.csproj
dotnet build   DriftCore/DriftCore.csproj -c Release

# Or open DriftCore.sln in Visual Studio and press F5
```

The app targets `net8.0-windows` and must be compiled as `x64`.

---

## How It Works

```
Physical Controller
        │
        ▼
  XInputReader (xinput1_4.dll P/Invoke)
        │  raw stick [-1, 1]
        ▼
  DriftCoreEngine
    ├─ Stage 1: Learned-center correction
    ├─ Stage 2: Idle detection + adaptive learning
    ├─ Stage 3: Spike suppression
    ├─ Stage 4: Deadzone with hysteresis
    └─ Stage 5: EMA smoothing
        │  corrected stick [-1, 1]
        ▼
  VirtualControllerManager (ViGEm)
        │
        ▼
  Game (sees perfect virtual Xbox controller)
```

---

## Settings Reference

| Setting | Default | Effect |
|---|---|---|
| Deadzone Radius | 0.08 | Physical deadzone applied after correction |
| Hysteresis Band | 0.025 | Prevents jitter at deadzone edge |
| Smoothing | 0.30 | EMA smoothing — higher = smoother but more lag |
| Learning Rate | 0.0008 | Speed at which center drift is learned |
| Spike Threshold | 0.35 | Jump magnitude classified as an intermittent spike |

### Calibrate Now
Snaps the learned center to the current stick position instantly.  
Use while the stick is at rest for best results.

---

## Architecture

```
DriftCore/
├── Core/
│   └── DriftCoreEngine.cs      Signal processing pipeline
├── Input/
│   └── XInputReader.cs         XInput P/Invoke wrapper
├── Output/
│   └── VirtualControllerManager.cs   ViGEm output
├── MainWindow.xaml             Dark gaming UI
├── MainWindow.xaml.cs          60 Hz game loop + canvas drawing
└── App.xaml                    Global styles and theme
```

---

## Roadmap

- [ ] Per-game profiles
- [ ] Settings persistence (JSON)
- [ ] PlayStation controller support (DualSense via HID)
- [ ] System tray mode
- [ ] Input latency graph
- [ ] Drift severity meter
- [ ] Auto-start with Windows

---

## Troubleshooting

**"ViGEmBus not installed" warning**  
Install ViGEmBus from the link above and reboot.

**Controller not detected**  
Press `RECONNECT CONTROLLER`. Make sure the controller is plugged in 
before launching the app.

**Game sees both controllers**  
Install HidHide and whitelist `DriftCore.exe`.

**Correction feels laggy**  
Lower the Smoothing slider. At 0.0 there is no smoothing whatsoever.

**Drift still visible after correction**  
Press `CALIBRATE NOW` while the stick is at rest, or increase the 
Deadzone Radius slightly.
