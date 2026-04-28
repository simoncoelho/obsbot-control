namespace Obsbot.Control;

public sealed record AimTarget(
    string Name,
    int? Pan = null,
    int? Tilt = null,
    int? Zoom = null,
    double? ImageX = null,
    double? ImageY = null);
