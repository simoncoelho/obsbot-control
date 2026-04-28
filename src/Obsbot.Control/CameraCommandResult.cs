namespace Obsbot.Control;

public sealed record CameraCommandResult(
    Guid CommandId,
    string CommandName,
    ObsbotTransport Transport,
    AimTarget? Target,
    DateTimeOffset SentAtUtc,
    DateTimeOffset CompletedAtUtc,
    DateTimeOffset? FirstObservedChangeAtUtc,
    bool Success,
    string Message,
    ObsbotCameraState? StartingState,
    ObsbotCameraState? EndingState)
{
    public double ElapsedMilliseconds => (CompletedAtUtc - SentAtUtc).TotalMilliseconds;
}
