namespace Obsbot.Control;

public sealed record CameraControlRange(
    CameraControlKind Kind,
    int Min,
    int Max,
    int Step,
    int Default,
    int Flags,
    bool IsSupported,
    string Source);
