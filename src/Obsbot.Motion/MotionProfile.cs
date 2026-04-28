namespace Obsbot.Motion;

public sealed record MotionProfile(
    string Name,
    int MinPanSpeed = 1,
    int MaxPanSpeed = 8,
    int MinTiltSpeed = 1,
    int MaxTiltSpeed = 8,
    int Tolerance = 1,
    int SlowZone = 18,
    MotionControlMode ControlMode = MotionControlMode.AbsoluteSetpoint,
    int MaxPanStepChange = 0,
    int MaxTiltStepChange = 0,
    int StableSamples = 3,
    int NoProgressSamples = 8,
    TimeSpan SampleInterval = default,
    TimeSpan Timeout = default)
{
    public TimeSpan EffectiveSampleInterval => SampleInterval == default ? TimeSpan.FromMilliseconds(20) : SampleInterval;
    public TimeSpan EffectiveTimeout => Timeout == default ? TimeSpan.FromSeconds(3) : Timeout;

    public static MotionProfile Conservative { get; } = new(
        "conservative",
        MinPanSpeed: 1,
        MaxPanSpeed: 2,
        MinTiltSpeed: 1,
        MaxTiltSpeed: 2,
        Tolerance: 2,
        SlowZone: 14,
        StableSamples: 3,
        NoProgressSamples: 10,
        SampleInterval: TimeSpan.FromMilliseconds(90),
        Timeout: TimeSpan.FromSeconds(3));
}
