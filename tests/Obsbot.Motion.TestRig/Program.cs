using System.ComponentModel;
using System.Diagnostics;
using Obsbot.Control;
using Obsbot.Motion;

var logRoot = Path.Combine(
    Directory.GetCurrentDirectory(),
    "motion-runs",
    DateTimeOffset.Now.ToString("yyyyMMdd_HHmmss"));
Directory.CreateDirectory(logRoot);

var panDelta = ReadArg(args, "--pan-delta", 85);
var tiltDelta = ReadArg(args, "--tilt-delta", 45);
var timeoutMs = ReadArg(args, "--timeout-ms", 6000);
var dwellMs = ReadArg(args, "--dwell-ms", 180);
var probeOnly = HasArg(args, "--probe-only");
var keepVideoAlive = !HasArg(args, "--no-video-keepalive");

await using var driver = new DirectShowObsbotCameraDriver(maxLogEntries: 1000, maxCommandEntries: 500);
driver.LogAdded += entry =>
{
    Console.WriteLine($"{entry.TimestampUtc:HH:mm:ss.fff} [{entry.Level}] {entry.Source}: {entry.Message} {entry.Raw}");
};

Console.WriteLine($"Log directory: {logRoot}");
using var videoKeepalive = keepVideoAlive ? StartVideoKeepalive(logRoot) : null;
if (videoKeepalive is not null)
{
    Console.WriteLine($"Video keepalive started with PID {videoKeepalive.Id}.");
    await Task.Delay(750);
}

Console.WriteLine("Connecting to OBSBOT camera...");
await driver.ConnectAsync();
var state = await driver.RefreshStateAsync();
if (!state.IsConnected)
{
    Console.WriteLine($"Camera not connected: {state.LastError}");
    StopVideoKeepalive(videoKeepalive);
    Environment.ExitCode = 2;
    return;
}

var pan = Read(state, CameraControlKind.Pan) ?? throw new InvalidOperationException("Pan is unavailable.");
var tilt = Read(state, CameraControlKind.Tilt) ?? throw new InvalidOperationException("Tilt is unavailable.");
var zoom = Read(state, CameraControlKind.Zoom);
var panRange = state.Controls[CameraControlKind.Pan].Range;
var tiltRange = state.Controls[CameraControlKind.Tilt].Range;

var home = new TeachPoint("home", pan, tilt, zoom);
var targets = BuildWideTargetRoute(home, panRange, tiltRange, panDelta, tiltDelta);
var profiles = new[]
{
    new MotionProfile(
        "single_firmware",
        MinPanSpeed: 1,
        MaxPanSpeed: 4,
        MinTiltSpeed: 1,
        MaxTiltSpeed: 4,
        Tolerance: 5,
        SlowZone: 18,
        ControlMode: MotionControlMode.AbsoluteSingleShot,
        MaxPanStepChange: 1,
        MaxTiltStepChange: 1,
        StableSamples: 2,
        NoProgressSamples: 40,
        SampleInterval: TimeSpan.FromMilliseconds(30),
        Timeout: TimeSpan.FromMilliseconds(timeoutMs)),
    new MotionProfile(
        "setpoint_glide",
        MinPanSpeed: 2,
        MaxPanSpeed: 8,
        MinTiltSpeed: 2,
        MaxTiltSpeed: 7,
        Tolerance: 5,
        SlowZone: 24,
        MaxPanStepChange: 3,
        MaxTiltStepChange: 3,
        StableSamples: 2,
        NoProgressSamples: 14,
        SampleInterval: TimeSpan.FromMilliseconds(35),
        Timeout: TimeSpan.FromMilliseconds(timeoutMs)),
    new MotionProfile(
        "setpoint_fast",
        MinPanSpeed: 4,
        MaxPanSpeed: 16,
        MinTiltSpeed: 3,
        MaxTiltSpeed: 13,
        Tolerance: 6,
        SlowZone: 32,
        MaxPanStepChange: 4,
        MaxTiltStepChange: 4,
        StableSamples: 2,
        NoProgressSamples: 20,
        SampleInterval: TimeSpan.FromMilliseconds(25),
        Timeout: TimeSpan.FromMilliseconds(timeoutMs)),
    new MotionProfile(
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
        Timeout: TimeSpan.FromMilliseconds(timeoutMs)),
    new MotionProfile(
        "setpoint_overdrive",
        MinPanSpeed: 14,
        MaxPanSpeed: 42,
        MinTiltSpeed: 10,
        MaxTiltSpeed: 30,
        Tolerance: 10,
        SlowZone: 70,
        MaxPanStepChange: 12,
        MaxTiltStepChange: 10,
        StableSamples: 2,
        NoProgressSamples: 28,
        SampleInterval: TimeSpan.FromMilliseconds(15),
        Timeout: TimeSpan.FromMilliseconds(timeoutMs))
};

Console.WriteLine($"Start pan={pan}, tilt={tilt}, zoom={zoom}");
if (probeOnly)
{
    await RunKsProbeAsync(driver, logRoot);
    StopVideoKeepalive(videoKeepalive);
    return;
}

Console.WriteLine($"Targets: {string.Join(", ", targets.Select(t => $"{t.Name}({t.Pan},{t.Tilt})"))}");

var controller = new TeachPointMotionController(driver);
var runs = new List<MotionRun>();
try
{
    foreach (var profile in profiles)
    {
        Console.WriteLine($"Profile {profile.Name}: max pan/tilt step {profile.MaxPanSpeed}/{profile.MaxTiltSpeed}, interval {profile.EffectiveSampleInterval.TotalMilliseconds:0}ms");
        foreach (var target in targets)
        {
            var run = await controller.MoveToAsync(target, profile);
            runs.Add(run);
            await MotionRunLogWriter.WriteRunAsync(run, logRoot);

            Console.WriteLine(
                $"{run.Name}: success={run.Success}, duration={run.DurationMilliseconds:0}ms, " +
                $"final=({run.FinalPan},{run.FinalTilt}), error=({run.FinalPanError},{run.FinalTiltError}), samples={run.Samples.Count}");

            await Task.Delay(dwellMs);
        }
    }
}
finally
{
    await driver.SetRelativeVelocityAsync(0, 0);
    await MotionRunLogWriter.WriteSummaryAsync(runs, logRoot);
    StopVideoKeepalive(videoKeepalive);
}

var successes = runs.Count(run => run.Success);
Console.WriteLine($"Completed {runs.Count} runs, {successes} successful. Logs: {logRoot}");
Environment.ExitCode = successes == runs.Count ? 0 : 1;

static int ReadArg(string[] args, string name, int fallback)
{
    var index = Array.IndexOf(args, name);
    if (index >= 0 && index + 1 < args.Length && int.TryParse(args[index + 1], out var value))
    {
        return value;
    }

    return fallback;
}

static bool HasArg(string[] args, string name)
{
    return args.Any(arg => string.Equals(arg, name, StringComparison.OrdinalIgnoreCase));
}

static int? Read(ObsbotCameraState state, CameraControlKind kind)
{
    return state.Controls.TryGetValue(kind, out var reading) ? reading.Value : null;
}

static int Clamp(int value, CameraControlRange? range)
{
    return range is null ? value : Math.Clamp(value, range.Min, range.Max);
}

static IReadOnlyList<TeachPoint> BuildWideTargetRoute(
    TeachPoint home,
    CameraControlRange? panRange,
    CameraControlRange? tiltRange,
    int panDelta,
    int tiltDelta)
{
    var panMagnitude = Math.Max(12, panDelta);
    var leftPan = Clamp(-panMagnitude, panRange);
    var centerPan = Clamp(0, panRange);
    var rightPan = Clamp(panMagnitude, panRange);
    var leftMidPan = Lerp(home.Pan, leftPan, 0.67);
    var rightNearPan = Lerp(home.Pan, rightPan, 0.33);
    var rightMidPan = Lerp(home.Pan, rightPan, 0.67);
    var highTilt = Clamp(home.Tilt + tiltDelta, tiltRange);
    var lowTilt = Clamp(home.Tilt - tiltDelta, tiltRange);
    var highMidTilt = Lerp(home.Tilt, highTilt, 0.67);
    var lowMidTilt = Lerp(home.Tilt, lowTilt, 0.67);
    var candidates = new[]
    {
        Point("home", home.Pan, home.Tilt),
        Point("right_near", rightNearPan, home.Tilt),
        Point("right_mid", rightMidPan, home.Tilt),
        Point("right_far", rightPan, home.Tilt),
        Point("right_high", rightPan, highMidTilt),
        Point("center_high", centerPan, highTilt),
        Point("left_high", leftPan, highMidTilt),
        Point("left_far", leftPan, home.Tilt),
        Point("left_low", leftPan, lowMidTilt),
        Point("center_low", centerPan, lowTilt),
        Point("right_low", rightPan, lowMidTilt),
        Point("home", home.Pan, home.Tilt),
        Point("right_mid_high", rightMidPan, highMidTilt),
        Point("left_mid_low", leftMidPan, lowMidTilt),
        Point("left_mid_high", leftMidPan, highMidTilt),
        Point("right_mid_low", rightMidPan, lowMidTilt),
        Point("right_far", rightPan, home.Tilt),
        Point("left_far", leftPan, home.Tilt),
        Point("center_high", centerPan, highTilt),
        Point("center_low", centerPan, lowTilt),
        Point("home", home.Pan, home.Tilt)
    };

    var route = new List<TeachPoint>();
    foreach (var candidate in candidates)
    {
        var previous = route.LastOrDefault();
        if (previous is null || previous.Pan != candidate.Pan || previous.Tilt != candidate.Tilt)
        {
            route.Add(candidate);
        }
    }

    return route;

    TeachPoint Point(string name, int targetPan, int targetTilt)
    {
        return new TeachPoint(
            name,
            Clamp(targetPan, panRange),
            Clamp(targetTilt, tiltRange),
            home.Zoom);
    }

    static int Lerp(int start, int end, double amount)
    {
        return (int)Math.Round(start + ((end - start) * amount));
    }
}

static async Task RunKsProbeAsync(DirectShowObsbotCameraDriver driver, string logRoot)
{
    const int flagsManualRelative = 0x12;
    var rows = new List<string>
    {
        "label,property_id,payload,value1,value2,flags,before_pan,before_tilt,after_pan,after_tilt,pan_delta,tilt_delta,success,message"
    };

    var cases = new (string Label, int PropertyId, KsCameraControlPayload Payload, int Value1, int? Value2, int Flags)[]
    {
        ("pan_relative_single_pos", 10, KsCameraControlPayload.ValueFlagsCapabilities, 4, null, flagsManualRelative),
        ("pan_relative_single_neg", 10, KsCameraControlPayload.ValueFlagsCapabilities, -4, null, flagsManualRelative),
        ("tilt_relative_single_pos", 11, KsCameraControlPayload.ValueFlagsCapabilities, 4, null, flagsManualRelative),
        ("tilt_relative_single_neg", 11, KsCameraControlPayload.ValueFlagsCapabilities, -4, null, flagsManualRelative),
        ("pan_relative_property_pos", 10, KsCameraControlPayload.PropertyValueFlagsCapabilities, 4, null, flagsManualRelative),
        ("pan_relative_property_neg", 10, KsCameraControlPayload.PropertyValueFlagsCapabilities, -4, null, flagsManualRelative),
        ("tilt_relative_property_pos", 11, KsCameraControlPayload.PropertyValueFlagsCapabilities, 4, null, flagsManualRelative),
        ("tilt_relative_property_neg", 11, KsCameraControlPayload.PropertyValueFlagsCapabilities, -4, null, flagsManualRelative),
        ("pantilt_relative_s2_pos", 17, KsCameraControlPayload.Value1FlagsCapabilitiesValue2, 4, 3, flagsManualRelative),
        ("pantilt_relative_s2_neg", 17, KsCameraControlPayload.Value1FlagsCapabilitiesValue2, -4, -3, flagsManualRelative),
        ("pantilt_relative_alt_pos", 17, KsCameraControlPayload.Value1Value2FlagsCapabilities, 4, 3, flagsManualRelative),
        ("pantilt_relative_alt_neg", 17, KsCameraControlPayload.Value1Value2FlagsCapabilities, -4, -3, flagsManualRelative),
        ("pantilt_relative_property_pos", 17, KsCameraControlPayload.PropertyValue1FlagsCapabilitiesValue2, 4, 3, flagsManualRelative),
        ("pantilt_relative_property_neg", 17, KsCameraControlPayload.PropertyValue1FlagsCapabilitiesValue2, -4, -3, flagsManualRelative)
    };

    foreach (var testCase in cases)
    {
        var before = await driver.RefreshStateAsync();
        var beforePan = Read(before, CameraControlKind.Pan);
        var beforeTilt = Read(before, CameraControlKind.Tilt);
        var result = await driver.SetKsCameraControlAsync(testCase.PropertyId, testCase.Value1, testCase.Value2, testCase.Flags, testCase.Payload);
        await Task.Delay(350);
        await driver.SetRelativeVelocityAsync(0, 0);
        var after = await driver.RefreshStateAsync();
        var afterPan = Read(after, CameraControlKind.Pan);
        var afterTilt = Read(after, CameraControlKind.Tilt);
        var panDelta = afterPan - beforePan;
        var tiltDelta = afterTilt - beforeTilt;

        rows.Add(string.Join(
            ',',
            Csv(testCase.Label),
            testCase.PropertyId,
            testCase.Payload,
            testCase.Value1,
            testCase.Value2?.ToString() ?? string.Empty,
            $"0x{testCase.Flags:X}",
            beforePan?.ToString() ?? string.Empty,
            beforeTilt?.ToString() ?? string.Empty,
            afterPan?.ToString() ?? string.Empty,
            afterTilt?.ToString() ?? string.Empty,
            panDelta?.ToString() ?? string.Empty,
            tiltDelta?.ToString() ?? string.Empty,
            result.Success,
            Csv(result.Message)));

        Console.WriteLine($"{testCase.Label}: success={result.Success}, before=({beforePan},{beforeTilt}), after=({afterPan},{afterTilt}), delta=({panDelta},{tiltDelta})");
        await Task.Delay(200);
    }

    var path = Path.Combine(logRoot, "ks-probe.csv");
    await File.WriteAllLinesAsync(path, rows);
    Console.WriteLine($"KS probe log: {path}");
}

static string Csv(string value)
{
    return '"' + value.Replace("\"", "\"\"", StringComparison.Ordinal) + '"';
}

static Process? StartVideoKeepalive(string logRoot)
{
    var errorPath = Path.Combine(logRoot, "ffmpeg-keepalive.err.log");
    var outputPath = Path.Combine(logRoot, "ffmpeg-keepalive.out.log");
    var process = new Process
    {
        StartInfo = new ProcessStartInfo
        {
            FileName = "ffmpeg",
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        },
        EnableRaisingEvents = true
    };

    process.StartInfo.ArgumentList.Add("-hide_banner");
    process.StartInfo.ArgumentList.Add("-loglevel");
    process.StartInfo.ArgumentList.Add("warning");
    process.StartInfo.ArgumentList.Add("-f");
    process.StartInfo.ArgumentList.Add("dshow");
    process.StartInfo.ArgumentList.Add("-video_size");
    process.StartInfo.ArgumentList.Add("1280x720");
    process.StartInfo.ArgumentList.Add("-framerate");
    process.StartInfo.ArgumentList.Add("30");
    process.StartInfo.ArgumentList.Add("-i");
    process.StartInfo.ArgumentList.Add("video=OBSBOT Tiny SE StreamCamera");
    process.StartInfo.ArgumentList.Add("-an");
    process.StartInfo.ArgumentList.Add("-f");
    process.StartInfo.ArgumentList.Add("null");
    process.StartInfo.ArgumentList.Add("-");

    try
    {
        process.Start();
    }
    catch (Win32Exception ex)
    {
        File.WriteAllText(errorPath, $"Failed to start ffmpeg keepalive: {ex.Message}");
        process.Dispose();
        return null;
    }

    _ = Task.Run(async () =>
    {
        var stderr = await process.StandardError.ReadToEndAsync();
        await File.WriteAllTextAsync(errorPath, stderr);
    });

    _ = Task.Run(async () =>
    {
        var stdout = await process.StandardOutput.ReadToEndAsync();
        await File.WriteAllTextAsync(outputPath, stdout);
    });

    return process;
}

static void StopVideoKeepalive(Process? process)
{
    if (process is null)
    {
        return;
    }

    try
    {
        if (!process.HasExited)
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit(2000);
        }
    }
    catch (InvalidOperationException)
    {
    }
    catch (Win32Exception)
    {
    }
}
