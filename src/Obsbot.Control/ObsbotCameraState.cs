namespace Obsbot.Control;

public sealed record ObsbotCameraState(
    bool IsConnected,
    ObsbotDeviceInfo? Device,
    IReadOnlyDictionary<CameraControlKind, CameraControlReading> Controls,
    DateTimeOffset LastUpdatedUtc,
    string? LastError,
    bool IsPolling)
{
    public static ObsbotCameraState Disconnected { get; } = new(
        false,
        null,
        new Dictionary<CameraControlKind, CameraControlReading>(),
        DateTimeOffset.UtcNow,
        null,
        false);
}
