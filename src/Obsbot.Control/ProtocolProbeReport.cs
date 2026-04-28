namespace Obsbot.Control;

public sealed record ProtocolProbeReport(
    DateTimeOffset CreatedAtUtc,
    IReadOnlyList<CameraControlRange> StandardUvcControls,
    IReadOnlyList<string> ExtensionUnitCandidates,
    IReadOnlyList<string> CaptureToolStatus,
    IReadOnlyList<string> Notes);
