# Agent Notes

This repo is intended to ship as a clean OBSBOT Tiny SE control workspace.

## Source Layout

- `src/Obsbot.Control`: low-level Windows DirectShow/UVC camera control. No OBSBOT SDK.
- `src/Obsbot.Motion`: reusable teachpoint motion controller and motion telemetry.
- `src/Obsbot.Control.Lab`: Blazor Server investigation UI.
- `tests/Obsbot.Motion.TestRig`: hardware sweep runner for validating profiles against a connected camera.
- `examples/TeachPointConsole`: minimal consumer example for the reusable libraries.
- `src/Obsbot.Control.Lab/Components/Pages/ObsbotLab.razor`: lab page.
- `src/Obsbot.Control.Lab/Services/FfmpegCameraStreamService.cs`: host-side MJPEG stream endpoint for the lab UI.

## Preferred Motion Path

Use `TeachPointMotionController` with `MotionProfile` from `Obsbot.Motion`.

Current proven default:

- `MotionControlMode.AbsoluteSetpoint`
- profile values matching `setpoint_limit` in `src/Obsbot.Control.Lab/Components/Pages/ObsbotLab.razor` or `tests/Obsbot.Motion.TestRig/Program.cs`

Keep `AbsoluteSingleShot` available when visual smoothness matters more than reaching every edge target.

## Paths To Avoid As Defaults

The low-level driver still exposes relative velocity and KS probe APIs for investigation. Do not use those as the default aiming path unless new hardware tests prove they work reliably.

## Verification

Use:

```powershell
dotnet build obsbot-control.sln -m:1
```

For hardware sweeps:

```powershell
dotnet run --project tests\Obsbot.Motion.TestRig\Obsbot.Motion.TestRig.csproj -- --timeout-ms 8000 --dwell-ms 40
```

The test rig starts an FFmpeg keepalive stream by default. Disable with `--no-video-keepalive` only when testing stream interactions.
