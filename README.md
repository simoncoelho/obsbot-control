# OBSBOT Tiny SE Direct Control

Windows-only .NET control workspace for the OBSBOT Tiny SE camera.

The repository has three intentional pieces:

- `src/Obsbot.Control`: low-level direct camera access through Windows DirectShow/IAMCameraControl. This library does not use the OBSBOT SDK.
- `src/Obsbot.Motion`: reusable teachpoint motion controller built on top of `Obsbot.Control`.
- `src/Obsbot.Control.Lab`: Blazor Server lab UI for live diagnostics, camera preview, teachpoint capture, and motion profile testing.
- `tests/Obsbot.Motion.TestRig`: hardware sweep runner for validating motion profiles against a connected camera.
- `examples/TeachPointConsole`: minimal console example for using the reusable control and motion libraries.

## Requirements

- Windows host with the OBSBOT Tiny SE connected as a standard webcam.
- .NET 10 SDK.
- FFmpeg on `PATH` for the preferred MJPEG preview stream.

## Build

```powershell
dotnet build obsbot-control.sln -m:1
```

## Run The Lab

```powershell
dotnet run --project src\Obsbot.Control.Lab\Obsbot.Control.Lab.csproj --urls http://127.0.0.1:5100
```

Open `http://localhost:5100/obsbot`.

## Use The Control Library Elsewhere

For reusable motion code, reference both projects:

```xml
<ProjectReference Include="..\src\Obsbot.Control\Obsbot.Control.csproj" />
<ProjectReference Include="..\src\Obsbot.Motion\Obsbot.Motion.csproj" />
```

The recommended high-level entry point is `TeachPointMotionController`.

```csharp
await using var driver = new DirectShowObsbotCameraDriver();
await driver.ConnectAsync();

var controller = new TeachPointMotionController(driver);
var home = await controller.CaptureTeachPointAsync("home");

var fastProfile = new MotionProfile(
    "setpoint_limit",
    MinPanSpeed: 10,
    MaxPanSpeed: 28,
    MinTiltSpeed: 6,
    MaxTiltSpeed: 20,
    Tolerance: 8,
    SlowZone: 55,
    MaxPanStepChange: 8,
    MaxTiltStepChange: 7,
    StableSamples: 2,
    NoProgressSamples: 24,
    SampleInterval: TimeSpan.FromMilliseconds(18),
    Timeout: TimeSpan.FromSeconds(8));

var run = await controller.MoveToAsync(home, fastProfile);
Console.WriteLine($"{run.Success}: {run.DurationMilliseconds:0} ms, error {run.FinalPanError}/{run.FinalTiltError}");
```

More detailed examples are in [docs/control-library.md](docs/control-library.md).

## Current Motion Guidance

Hardware testing showed:

- Best reliable speed profile so far: `setpoint_limit`.
- Smoothest visual behavior for many moves: `single_firmware`, but it can miss deep downward tilt targets.
- Direct UVC relative velocity is exposed at the low level for investigation, but it did not behave consistently enough to be used by the high-level motion controller.

The Blazor lab exposes the proven high-level profiles and records teachpoints directly from live camera state.
