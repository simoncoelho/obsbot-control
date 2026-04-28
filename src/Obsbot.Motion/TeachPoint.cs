namespace Obsbot.Motion;

public sealed record TeachPoint(
    string Name,
    int Pan,
    int Tilt,
    int? Zoom = null);
