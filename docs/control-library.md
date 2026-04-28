# Control Library Guide

This guide is for agents or developers consuming the OBSBOT Tiny SE control code from another project.

## Projects

### `Obsbot.Control`

Low-level Windows camera driver.

Primary type:

```csharp
DirectShowObsbotCameraDriver
```

Use it for:

- Device discovery.
- Connecting to the OBSBOT camera.
- Reading live pan, tilt, zoom, focus, and exposure state.
- Sending direct UVC absolute pan/tilt/zoom commands.
- Capturing named raw `AimTarget` values.
- Protocol probing and capture analysis hooks.

### `Obsbot.Motion`

High-level teachpoint controller.

Primary type:

```csharp
TeachPointMotionController
```

Use it for:

- Capturing teachpoints from current live state.
- Moving to teachpoints with tested motion profiles.
- Running routines over saved teachpoints.
- Recording telemetry for each move.

## Basic Connection

```csharp
await using var driver = new DirectShowObsbotCameraDriver();

driver.LogAdded += entry =>
{
    Console.WriteLine($"{entry.TimestampUtc:HH:mm:ss.fff} [{entry.Level}] {entry.Source}: {entry.Message}");
};

await driver.ConnectAsync();
var state = await driver.RefreshStateAsync();

if (!state.IsConnected)
{
    throw new InvalidOperationException(state.LastError ?? "Camera failed to connect.");
}
```

## Read Live State

```csharp
static int? Read(ObsbotCameraState state, CameraControlKind kind)
{
    return state.Controls.TryGetValue(kind, out var reading)
        ? reading.Value
        : null;
}

var state = await driver.RefreshStateAsync();
var pan = Read(state, CameraControlKind.Pan);
var tilt = Read(state, CameraControlKind.Tilt);
var zoom = Read(state, CameraControlKind.Zoom);

Console.WriteLine($"pan={pan}, tilt={tilt}, zoom={zoom}");
```

## Capture A Teachpoint

Teachpoints are the preferred reusable representation for learned camera positions.

```csharp
var controller = new TeachPointMotionController(driver);
var stageLeft = await controller.CaptureTeachPointAsync("stage-left");

Console.WriteLine($"{stageLeft.Name}: pan={stageLeft.Pan}, tilt={stageLeft.Tilt}, zoom={stageLeft.Zoom}");
```

## Move To A Teachpoint

```csharp
var run = await controller.MoveToAsync(stageLeft, MotionProfiles.SetpointLimit);

if (!run.Success)
{
    Console.WriteLine(run.Message);
}

Console.WriteLine($"duration={run.DurationMilliseconds:0}ms final={run.FinalPan},{run.FinalTilt} error={run.FinalPanError}/{run.FinalTiltError}");
```

## Recommended Profiles

The library does not force one global profile. Keep profile definitions near the app behavior you want.

```csharp
public static class MotionProfiles
{
    public static MotionProfile SingleFirmware { get; } = new(
        "single_firmware",
        Tolerance: 5,
        ControlMode: MotionControlMode.AbsoluteSingleShot,
        StableSamples: 2,
        NoProgressSamples: 40,
        SampleInterval: TimeSpan.FromMilliseconds(30),
        Timeout: TimeSpan.FromSeconds(8));

    public static MotionProfile SetpointLimit { get; } = new(
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
}
```

Profile guidance from hardware testing:

- `SingleFirmware` sends one absolute target and lets camera firmware handle the move. It often looks smooth, but missed deep downward tilt targets during testing.
- `SetpointLimit` is the best current default for fast, reliable movement.
- Larger values than `SetpointLimit` did not improve average movement time in testing.

## Run A Teachpoint Routine

```csharp
var points = new[]
{
    await controller.CaptureTeachPointAsync("wide"),
    await controller.CaptureTeachPointAsync("desk"),
    await controller.CaptureTeachPointAsync("center")
};

foreach (var point in points)
{
    var run = await controller.MoveToAsync(point, MotionProfiles.SetpointLimit);
    Console.WriteLine($"{run.Name}: {run.Success}, {run.DurationMilliseconds:0}ms");
    await Task.Delay(250);
}
```

## Low-Level Direct Aim

Use `AimAtAsync` when you intentionally want one direct UVC absolute command without high-level motion telemetry.

```csharp
await driver.AimAtAsync(new AimTarget("center", Pan: 0, Tilt: 0, Zoom: 0));
```

## Low-Level Experimental Controls

`DirectShowObsbotCameraDriver` exposes `AimAtSpeedAsync`, `SetRelativeVelocityAsync`, and `SetKsCameraControlAsync` for investigation. They remain in the low-level library because they are useful for protocol discovery.

Do not use them as default production motion paths yet. Testing showed the standard UVC relative velocity path was not reliable enough for high-level camera aiming on this device.

## Operational Notes

- Keep an active video stream open while testing motion. FFmpeg DirectShow capture acted as a practical keepalive during hardware tests.
- Always inspect `MotionRun.Success`, `Message`, `FinalPanError`, and `FinalTiltError`.
- `MotionRun.Samples` contains the observed state sequence and setpoints sent during a move.
- The high-level motion controller clamps behavior through observed state, not assumed device physics.
