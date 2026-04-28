namespace Obsbot.Motion;

public sealed record MotionSample(
    TimeSpan Elapsed,
    int? Pan,
    int? Tilt,
    int PanError,
    int TiltError,
    int PanStep,
    int TiltStep,
    string Event);
