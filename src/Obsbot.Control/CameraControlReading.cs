namespace Obsbot.Control;

public sealed record CameraControlReading(
    CameraControlKind Kind,
    int? Value,
    int? Flags,
    CameraControlRange? Range,
    string Source,
    string? Error = null);
