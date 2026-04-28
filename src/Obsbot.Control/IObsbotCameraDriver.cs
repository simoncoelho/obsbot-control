namespace Obsbot.Control;

public interface IObsbotCameraDriver : IAsyncDisposable
{
    event Action<ObsbotCameraState>? StateChanged;
    event Action<DriverLogEntry>? LogAdded;
    event Action<CameraCommandResult>? CommandCompleted;

    ObsbotCameraState State { get; }
    IReadOnlyList<DriverLogEntry> Logs { get; }
    IReadOnlyList<CameraCommandResult> CommandHistory { get; }
    IReadOnlyList<AimTarget> NamedTargets { get; }
    IReadOnlyList<CalibrationSample> CalibrationSamples { get; }

    Task<IReadOnlyList<ObsbotDeviceInfo>> DiscoverDevicesAsync(CancellationToken cancellationToken = default);
    Task ConnectAsync(string? devicePath = null, CancellationToken cancellationToken = default);
    Task DisconnectAsync(CancellationToken cancellationToken = default);
    Task<ObsbotCameraState> RefreshStateAsync(CancellationToken cancellationToken = default);
    Task<CameraCommandResult> AimAtAsync(AimTarget target, CancellationToken cancellationToken = default);
    Task<CameraCommandResult> AimAtSpeedAsync(AimTarget target, int panSpeed, int tiltSpeed, int tolerance, TimeSpan timeout, CancellationToken cancellationToken = default);
    Task SetRelativeVelocityAsync(int panSpeed, int tiltSpeed, CancellationToken cancellationToken = default);
    Task<CameraCommandResult> MoveRelativeAsync(int panDelta, int tiltDelta, CancellationToken cancellationToken = default);
    Task<CameraCommandResult> SetZoomAsync(int zoom, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<CameraCommandResult>> RunRoutineAsync(IEnumerable<AimTarget> targets, TimeSpan dwell, CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
    Task<ProtocolProbeReport> ProbeProtocolAsync(CancellationToken cancellationToken = default);
    Task<CaptureAnalysisSummary> AnalyzeCaptureAsync(string fileName, Stream capture, CancellationToken cancellationToken = default);
    Task<AimTarget> CaptureNamedTargetAsync(string name, CancellationToken cancellationToken = default);
    Task<CalibrationSample> RecordCalibrationSampleAsync(string name, double imageX, double imageY, CancellationToken cancellationToken = default);
}
