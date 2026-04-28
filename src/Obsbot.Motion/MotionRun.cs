namespace Obsbot.Motion;

public sealed record MotionRun(
    Guid Id,
    string Name,
    TeachPoint Target,
    MotionProfile Profile,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset CompletedAtUtc,
    bool Success,
    string Message,
    int? StartPan,
    int? StartTilt,
    int? FinalPan,
    int? FinalTilt,
    IReadOnlyList<MotionSample> Samples)
{
    public double DurationMilliseconds => (CompletedAtUtc - StartedAtUtc).TotalMilliseconds;
    public int FinalPanError => Target.Pan - (FinalPan ?? Target.Pan);
    public int FinalTiltError => Target.Tilt - (FinalTilt ?? Target.Tilt);
}
