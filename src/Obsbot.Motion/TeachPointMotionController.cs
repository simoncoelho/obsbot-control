using System.Diagnostics;
using Obsbot.Control;

namespace Obsbot.Motion;

public sealed class TeachPointMotionController(IObsbotCameraDriver driver)
{
    public async Task<TeachPoint> CaptureTeachPointAsync(string name, CancellationToken cancellationToken = default)
    {
        var state = await driver.RefreshStateAsync(cancellationToken);
        var pan = Read(state, CameraControlKind.Pan) ?? throw new InvalidOperationException("Pan state is unavailable.");
        var tilt = Read(state, CameraControlKind.Tilt) ?? throw new InvalidOperationException("Tilt state is unavailable.");
        var zoom = Read(state, CameraControlKind.Zoom);
        return new TeachPoint(name, pan, tilt, zoom);
    }

    public async Task<MotionRun> MoveToAsync(TeachPoint target, MotionProfile profile, CancellationToken cancellationToken = default)
    {
        var id = Guid.NewGuid();
        var started = DateTimeOffset.UtcNow;
        var stopwatch = Stopwatch.StartNew();
        var samples = new List<MotionSample>(256);
        var state = await driver.RefreshStateAsync(cancellationToken);
        var startPan = Read(state, CameraControlKind.Pan);
        var startTilt = Read(state, CameraControlKind.Tilt);
        var stableCount = 0;
        var noProgressCount = 0;
        var success = false;
        var message = string.Empty;
        int? lastObservedPan = null;
        int? lastObservedTilt = null;
        var lastPanStep = 0;
        var lastTiltStep = 0;

        if (target.Zoom is int zoom)
        {
            await driver.SetZoomAsync(zoom, cancellationToken);
        }

        if (profile.ControlMode == MotionControlMode.AbsoluteSingleShot)
        {
            return await MoveToSingleShotAsync(
                id,
                target,
                profile,
                started,
                stopwatch,
                samples,
                startPan,
                startTilt,
                cancellationToken);
        }

        try
        {
            while (stopwatch.Elapsed < profile.EffectiveTimeout)
            {
                cancellationToken.ThrowIfCancellationRequested();
                state = await driver.RefreshStateAsync(cancellationToken);
                var pan = Read(state, CameraControlKind.Pan);
                var tilt = Read(state, CameraControlKind.Tilt);

                if (pan is null || tilt is null)
                {
                    message = "Pan or tilt state is unavailable during motion.";
                    samples.Add(Sample(stopwatch.Elapsed, pan, tilt, target, 0, 0, message));
                    break;
                }

                var panError = target.Pan - pan.Value;
                var tiltError = target.Tilt - tilt.Value;
                var settled = Math.Abs(panError) <= profile.Tolerance && Math.Abs(tiltError) <= profile.Tolerance;
                if (settled)
                {
                    stableCount++;
                    noProgressCount = 0;
                    samples.Add(Sample(stopwatch.Elapsed, pan, tilt, target, 0, 0, $"settled {stableCount}/{profile.StableSamples}"));
                    await driver.SetRelativeVelocityAsync(0, 0, cancellationToken);

                    if (stableCount >= profile.StableSamples)
                    {
                        success = true;
                        message = $"Reached {target.Name} within +/-{profile.Tolerance}.";
                        break;
                    }
                }
                else
                {
                    stableCount = 0;
                    if (lastObservedPan == pan && lastObservedTilt == tilt)
                    {
                        noProgressCount++;
                    }
                    else
                    {
                        noProgressCount = 0;
                    }

                    lastObservedPan = pan;
                    lastObservedTilt = tilt;

                    if (noProgressCount >= profile.NoProgressSamples)
                    {
                        message = $"No observed pan/tilt progress after {noProgressCount} samples; direct controls were accepted but live state did not move.";
                        samples.Add(Sample(stopwatch.Elapsed, pan, tilt, target, 0, 0, message));
                        break;
                    }

                    var desiredPanStep = StepFor(panError, profile.MinPanSpeed, profile.MaxPanSpeed, profile.SlowZone, profile.Tolerance);
                    var desiredTiltStep = StepFor(tiltError, profile.MinTiltSpeed, profile.MaxTiltSpeed, profile.SlowZone, profile.Tolerance);
                    var panStep = LimitStepChange(desiredPanStep, lastPanStep, profile.MaxPanStepChange);
                    var tiltStep = LimitStepChange(desiredTiltStep, lastTiltStep, profile.MaxTiltStepChange);
                    lastPanStep = panStep;
                    lastTiltStep = tiltStep;

                    var nextPan = AdvanceToward(pan.Value, target.Pan, panStep);
                    var nextTilt = AdvanceToward(tilt.Value, target.Tilt, tiltStep);

                    await driver.AimAtAsync(new AimTarget($"step {target.Name}", nextPan, nextTilt, null), cancellationToken);
                    samples.Add(Sample(
                        stopwatch.Elapsed,
                        pan,
                        tilt,
                        target,
                        panStep,
                        tiltStep,
                        $"absolute step to {nextPan},{nextTilt}; desired step {desiredPanStep},{desiredTiltStep}"));
                }

                await Task.Delay(profile.EffectiveSampleInterval, cancellationToken);
            }

            if (!success && string.IsNullOrWhiteSpace(message))
            {
                message = $"Timed out moving to {target.Name} after {profile.EffectiveTimeout.TotalMilliseconds:0} ms.";
            }
        }
        finally
        {
            await StopSafelyAsync();
        }

        state = await driver.RefreshStateAsync(CancellationToken.None);
        return new MotionRun(
            id,
            $"{target.Name}_{profile.Name}",
            target,
            profile,
            started,
            DateTimeOffset.UtcNow,
            success,
            message,
            startPan,
            startTilt,
            Read(state, CameraControlKind.Pan),
            Read(state, CameraControlKind.Tilt),
            samples);
    }

    private async Task<MotionRun> MoveToSingleShotAsync(
        Guid id,
        TeachPoint target,
        MotionProfile profile,
        DateTimeOffset started,
        Stopwatch stopwatch,
        List<MotionSample> samples,
        int? startPan,
        int? startTilt,
        CancellationToken cancellationToken)
    {
        var stableCount = 0;
        var noProgressCount = 0;
        var success = false;
        var message = string.Empty;
        int? lastObservedPan = null;
        int? lastObservedTilt = null;

        try
        {
            await driver.AimAtAsync(new AimTarget($"single {target.Name}", target.Pan, target.Tilt, null), cancellationToken);
            samples.Add(Sample(stopwatch.Elapsed, startPan, startTilt, target, target.Pan - (startPan ?? target.Pan), target.Tilt - (startTilt ?? target.Tilt), "single absolute command"));

            while (stopwatch.Elapsed < profile.EffectiveTimeout)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var state = await driver.RefreshStateAsync(cancellationToken);
                var pan = Read(state, CameraControlKind.Pan);
                var tilt = Read(state, CameraControlKind.Tilt);

                if (pan is null || tilt is null)
                {
                    message = "Pan or tilt state is unavailable during single-shot motion.";
                    samples.Add(Sample(stopwatch.Elapsed, pan, tilt, target, 0, 0, message));
                    break;
                }

                var panError = target.Pan - pan.Value;
                var tiltError = target.Tilt - tilt.Value;
                var settled = Math.Abs(panError) <= profile.Tolerance && Math.Abs(tiltError) <= profile.Tolerance;
                if (settled)
                {
                    stableCount++;
                    noProgressCount = 0;
                    samples.Add(Sample(stopwatch.Elapsed, pan, tilt, target, 0, 0, $"settled {stableCount}/{profile.StableSamples}"));
                    if (stableCount >= profile.StableSamples)
                    {
                        success = true;
                        message = $"Single-shot reached {target.Name} within +/-{profile.Tolerance}.";
                        break;
                    }
                }
                else
                {
                    stableCount = 0;
                    if (lastObservedPan == pan && lastObservedTilt == tilt)
                    {
                        noProgressCount++;
                    }
                    else
                    {
                        noProgressCount = 0;
                    }

                    lastObservedPan = pan;
                    lastObservedTilt = tilt;
                    if (noProgressCount >= profile.NoProgressSamples)
                    {
                        message = $"No observed pan/tilt progress after {noProgressCount} samples after single-shot command.";
                        samples.Add(Sample(stopwatch.Elapsed, pan, tilt, target, 0, 0, message));
                        break;
                    }

                    samples.Add(Sample(stopwatch.Elapsed, pan, tilt, target, 0, 0, "observing single-shot motion"));
                }

                await Task.Delay(profile.EffectiveSampleInterval, cancellationToken);
            }

            if (!success && string.IsNullOrWhiteSpace(message))
            {
                message = $"Timed out single-shot moving to {target.Name} after {profile.EffectiveTimeout.TotalMilliseconds:0} ms.";
            }
        }
        finally
        {
            await StopSafelyAsync();
        }

        var finalState = await driver.RefreshStateAsync(CancellationToken.None);
        return new MotionRun(
            id,
            $"{target.Name}_{profile.Name}",
            target,
            profile,
            started,
            DateTimeOffset.UtcNow,
            success,
            message,
            startPan,
            startTilt,
            Read(finalState, CameraControlKind.Pan),
            Read(finalState, CameraControlKind.Tilt),
            samples);
    }

    public async Task<IReadOnlyList<MotionRun>> CycleTeachPointsAsync(
        IReadOnlyList<TeachPoint> targets,
        IReadOnlyList<MotionProfile> profiles,
        string logDirectory,
        CancellationToken cancellationToken = default)
    {
        var runs = new List<MotionRun>();
        foreach (var profile in profiles)
        {
            foreach (var target in targets)
            {
                var run = await MoveToAsync(target, profile, cancellationToken);
                runs.Add(run);
                await MotionRunLogWriter.WriteRunAsync(run, logDirectory, cancellationToken);
            }
        }

        await MotionRunLogWriter.WriteSummaryAsync(runs, logDirectory, cancellationToken);
        return runs;
    }

    private static MotionSample Sample(TimeSpan elapsed, int? pan, int? tilt, TeachPoint target, int panStep, int tiltStep, string @event)
    {
        return new MotionSample(
            elapsed,
            pan,
            tilt,
            target.Pan - (pan ?? target.Pan),
            target.Tilt - (tilt ?? target.Tilt),
            panStep,
            tiltStep,
            @event);
    }

    private static int StepFor(int error, int minStep, int maxStep, int slowZone, int tolerance)
    {
        var magnitude = Math.Abs(error);
        if (magnitude <= tolerance)
        {
            return 0;
        }

        var clampedSlowZone = Math.Max(tolerance + 1, slowZone);
        var ratio = Math.Clamp((double)(magnitude - tolerance) / (clampedSlowZone - tolerance), 0, 1);
        var step = minStep + (int)Math.Round((Math.Max(minStep, maxStep) - minStep) * ratio);
        return Math.Sign(error) * Math.Max(1, Math.Min(magnitude, step));
    }

    private static int AdvanceToward(int current, int target, int signedStep)
    {
        if (signedStep == 0)
        {
            return current;
        }

        var next = current + signedStep;
        return signedStep > 0 ? Math.Min(next, target) : Math.Max(next, target);
    }

    private static int LimitStepChange(int desiredStep, int previousStep, int maxStepChange)
    {
        if (maxStepChange <= 0 || desiredStep == 0)
        {
            return desiredStep;
        }

        if (previousStep != 0 && Math.Sign(previousStep) != Math.Sign(desiredStep))
        {
            previousStep = 0;
        }

        var min = previousStep - maxStepChange;
        var max = previousStep + maxStepChange;
        var limited = Math.Clamp(desiredStep, min, max);
        if (limited == 0)
        {
            return Math.Sign(desiredStep);
        }

        return limited;
    }

    private static int? Read(ObsbotCameraState state, CameraControlKind kind)
    {
        return state.Controls.TryGetValue(kind, out var reading) ? reading.Value : null;
    }

    private async Task StopSafelyAsync()
    {
        try
        {
            await driver.SetRelativeVelocityAsync(0, 0, CancellationToken.None);
        }
        catch
        {
            // Preserve the motion result; stop failures are surfaced by the driver log.
        }
    }
}
