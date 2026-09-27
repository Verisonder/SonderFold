# SonderFold

A Windows app that animates your desktop as you close your laptop lid, in the style of the iPhone Duo fold animation.
It reads the lid angle from the screen's accelerometer and renders the effect live on the GPU.

Part of the Verisonder apps.

## Effects

- **Ghost** (default): the desktop appears to stay in place in space while the lid tilts, zooming out and softening toward the top.
- **Duo**: the desktop swells around the hinge and leaves through the top.

The effect starts from wherever the lid last rested, fades to black near the end of the close, and clears when the lid is held still.

## Requirements

- Windows 10 (2004) or Windows 11
- A laptop with an accelerometer in the screen (check with `Get-PnpDevice -Class Sensor`)
- .NET 8 SDK

Tested on a Dell Latitude 7450 (clamshell with touchscreen).

## Run

```powershell
dotnet run
```

The control window shows the live lid angle and every setting. Each setting has a slider and a number box with no upper limit. Minimise the window to keep it running; close it to exit.

## Settings

| Section | What it controls |
|---|---|
| Timing | Where the effect starts, when it clears, and where it fades to black |
| Sensitivity | Deadzone, stability, response speed and shake rejection, to stop wiggle when you let go of the lid |
| Look | Blur softness, shadow, and the Duo swell |
| Ghost perspective | Virtual eye distance and height, tilt strength, maximum tilt, horizontal and vertical squeeze, and an optional fixed desktop plane |

## How it works

- `LidSensor.cs` reads the accelerometer, converts it to a lid angle, and filters it (One Euro filter, deadzone, shake rejection).
- `FoldModel.cs` turns the lid angle into the effect's state: progress, tilt, blur and fade.
- `LiveScreenCapture.cs` captures the screen continuously and builds a small blur pyramid.
- `DuoEffect.cs` is the GPU pixel shader for both effects, compiled at startup with Windows' built-in shader compiler.
- `OverlayWindow` is a click-through, capture-excluded fullscreen window, so the PC stays usable during the animation.

## Credits

The Ghost and Duo effect maths are ported from [Mac Duo](https://github.com/DhananjayBhosale/MacDuo) by Dhananjay Bhosale (MIT). See `ATTRIBUTION.md` and `LICENSE-MacDuo.txt`.
