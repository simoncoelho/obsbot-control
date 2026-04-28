using Obsbot.Control;
using Obsbot.Motion;

var profile = new MotionProfile(
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

await using var driver = new DirectShowObsbotCameraDriver();
driver.LogAdded += entry => Console.WriteLine($"{entry.TimestampUtc:HH:mm:ss.fff} [{entry.Level}] {entry.Source}: {entry.Message}");

await driver.ConnectAsync();
var state = await driver.RefreshStateAsync();
if (!state.IsConnected)
{
    Console.Error.WriteLine(state.LastError ?? "Camera is not connected.");
    return 2;
}

var controller = new TeachPointMotionController(driver);
var home = await controller.CaptureTeachPointAsync("home");
var right = new TeachPoint("right", home.Pan + 20, home.Tilt, home.Zoom);

Console.WriteLine($"Captured home pan={home.Pan}, tilt={home.Tilt}, zoom={home.Zoom}");

var run = await controller.MoveToAsync(right, profile);
Console.WriteLine($"{run.Name}: success={run.Success}, duration={run.DurationMilliseconds:0}ms, final={run.FinalPan},{run.FinalTilt}, error={run.FinalPanError}/{run.FinalTiltError}");

await controller.MoveToAsync(home, profile);
return run.Success ? 0 : 1;
