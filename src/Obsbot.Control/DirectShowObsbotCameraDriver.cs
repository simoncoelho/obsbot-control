using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Buffers.Binary;
using System.Text.RegularExpressions;
using DirectShowLib;

namespace Obsbot.Control;

public sealed class DirectShowObsbotCameraDriver : IObsbotCameraDriver
{
    private const string ObsbotVendorId = "3564";
    private const string ObsbotProductId = "FEFF";
    private const int CameraControlPanRelative = 10;
    private const int CameraControlTiltRelative = 11;
    private const int KsPropertySupportGet = 0x00000001;
    private const int KsPropertySupportSet = 0x00000002;
    private static readonly Guid PropsetIdVidcapCameraControl = new("C6E13370-30AC-11D0-A18C-00A0C9118956");
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(50);
    private static readonly IReadOnlyDictionary<CameraControlKind, CameraControlProperty> CameraControlMap =
        new ReadOnlyDictionary<CameraControlKind, CameraControlProperty>(new Dictionary<CameraControlKind, CameraControlProperty>
        {
            [CameraControlKind.Pan] = CameraControlProperty.Pan,
            [CameraControlKind.Tilt] = CameraControlProperty.Tilt,
            [CameraControlKind.Zoom] = CameraControlProperty.Zoom,
            [CameraControlKind.Focus] = CameraControlProperty.Focus,
            [CameraControlKind.Exposure] = CameraControlProperty.Exposure
        });

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly List<DriverLogEntry> _logs = [];
    private readonly List<CameraCommandResult> _commands = [];
    private readonly List<AimTarget> _targets = [];
    private readonly List<CalibrationSample> _calibration = [];
    private readonly int _maxLogEntries;
    private readonly int _maxCommandEntries;
    private DirectShowCameraSession? _session;
    private CancellationTokenSource? _pollingCts;
    private Task? _pollingTask;
    private volatile bool _stopRequested;

    public DirectShowObsbotCameraDriver(int maxLogEntries = 300, int maxCommandEntries = 100)
    {
        _maxLogEntries = maxLogEntries;
        _maxCommandEntries = maxCommandEntries;
    }

    public event Action<ObsbotCameraState>? StateChanged;
    public event Action<DriverLogEntry>? LogAdded;
    public event Action<CameraCommandResult>? CommandCompleted;

    public ObsbotCameraState State { get; private set; } = ObsbotCameraState.Disconnected;
    public IReadOnlyList<DriverLogEntry> Logs => _logs;
    public IReadOnlyList<CameraCommandResult> CommandHistory => _commands;
    public IReadOnlyList<AimTarget> NamedTargets => _targets;
    public IReadOnlyList<CalibrationSample> CalibrationSamples => _calibration;

    public Task<IReadOnlyList<ObsbotDeviceInfo>> DiscoverDevicesAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var devices = DsDevice.GetDevicesOfCat(FilterCategory.VideoInputDevice)
            .Select((device, index) => ToDeviceInfo(device, index))
            .OrderByDescending(device => IsPreferredObsbot(device))
            .ThenBy(device => device.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        AddLog(DriverLogLevel.Information, "Discovery", $"Discovered {devices.Count} video input device(s).");
        return Task.FromResult<IReadOnlyList<ObsbotDeviceInfo>>(devices);
    }

    public async Task ConnectAsync(string? devicePath = null, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await DisconnectCoreAsync();

            var devices = DsDevice.GetDevicesOfCat(FilterCategory.VideoInputDevice).ToList();
            var selected = string.IsNullOrWhiteSpace(devicePath)
                ? devices.FirstOrDefault(device => IsPreferredObsbot(ToDeviceInfo(device))) ?? devices.FirstOrDefault()
                : devices.FirstOrDefault(device => string.Equals(device.DevicePath, devicePath, StringComparison.OrdinalIgnoreCase));

            if (selected is null)
            {
                State = ObsbotCameraState.Disconnected with
                {
                    LastUpdatedUtc = DateTimeOffset.UtcNow,
                    LastError = "No video input devices were found."
                };
                PublishState();
                AddLog(DriverLogLevel.Error, "Connect", State.LastError);
                return;
            }

            _session = DirectShowCameraSession.Open(selected);
            State = _session.ReadState(true, null);
            _stopRequested = false;
            AddLog(DriverLogLevel.Information, "Connect", $"Connected to {State.Device?.DisplayName ?? selected.Name}.");
            PublishState();
            StartPolling();
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or NotSupportedException)
        {
            State = ObsbotCameraState.Disconnected with
            {
                LastUpdatedUtc = DateTimeOffset.UtcNow,
                LastError = ex.Message
            };
            PublishState();
            AddLog(DriverLogLevel.Error, "Connect", "Failed to connect through DirectShow.", ex.Message);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await DisconnectCoreAsync();
            State = ObsbotCameraState.Disconnected with { LastUpdatedUtc = DateTimeOffset.UtcNow };
            PublishState();
            AddLog(DriverLogLevel.Information, "Disconnect", "Disconnected camera driver.");
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ObsbotCameraState> RefreshStateAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            State = _session?.ReadState(true, null) ?? ObsbotCameraState.Disconnected with { LastUpdatedUtc = DateTimeOffset.UtcNow };
            PublishState();
            return State;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<CameraCommandResult> AimAtAsync(AimTarget target, CancellationToken cancellationToken = default)
    {
        return await ExecuteCommandAsync("AimAt", target, session =>
        {
            var values = new List<KeyValuePair<CameraControlKind, int>>(3);
            if (target.Pan is int pan)
            {
                values.Add(KeyValuePair.Create(CameraControlKind.Pan, pan));
            }

            if (target.Tilt is int tilt)
            {
                values.Add(KeyValuePair.Create(CameraControlKind.Tilt, tilt));
            }

            if (target.Zoom is int zoom)
            {
                values.Add(KeyValuePair.Create(CameraControlKind.Zoom, zoom));
            }

            session.SetCameraValues(values);
            return $"Aimed at {target.Name}.";
        }, cancellationToken);
    }

    public async Task<CameraCommandResult> MoveRelativeAsync(int panDelta, int tiltDelta, CancellationToken cancellationToken = default)
    {
        var current = State;
        var pan = TryAddDelta(current, CameraControlKind.Pan, panDelta);
        var tilt = TryAddDelta(current, CameraControlKind.Tilt, tiltDelta);
        var target = new AimTarget($"Relative {panDelta:+#;-#;0}/{tiltDelta:+#;-#;0}", pan, tilt, CurrentValue(CameraControlKind.Zoom));
        return await AimAtAsync(target, cancellationToken);
    }

    public async Task SetRelativeVelocityAsync(int panSpeed, int tiltSpeed, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_session is null)
            {
                throw new InvalidOperationException("Camera is not connected.");
            }

            _session.SetRelativeVelocity(panSpeed, tiltSpeed);
            AddLog(DriverLogLevel.Trace, "Velocity", $"Set relative velocity pan={panSpeed}, tilt={tiltSpeed}.");
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<CameraCommandResult> SetKsCameraControlAsync(
        int propertyId,
        int value1,
        int? value2,
        int flags,
        KsCameraControlPayload payload,
        CancellationToken cancellationToken = default)
    {
        var target = new AimTarget($"KS {propertyId}/{payload}", CurrentValue(CameraControlKind.Pan), CurrentValue(CameraControlKind.Tilt), CurrentValue(CameraControlKind.Zoom));
        return await ExecuteCommandAsync("KsCameraControl", target, session =>
        {
            session.SetKsCameraControl(propertyId, value1, value2, flags, payload);
            return $"Set KS camera control property {propertyId} payload={payload} value1={value1} value2={value2?.ToString() ?? "null"} flags=0x{flags:X}.";
        }, cancellationToken);
    }

    public async Task<CameraCommandResult> AimAtSpeedAsync(
        AimTarget target,
        int panSpeed,
        int tiltSpeed,
        int tolerance,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        if (target.Pan is null && target.Tilt is null)
        {
            return await AimAtAsync(target, cancellationToken);
        }

        var commandId = Guid.NewGuid();
        var sent = DateTimeOffset.UtcNow;
        var deadline = sent + timeout;
        var normalizedPanSpeed = Math.Max(1, Math.Abs(panSpeed));
        var normalizedTiltSpeed = Math.Max(1, Math.Abs(tiltSpeed));
        var normalizedTolerance = Math.Max(0, tolerance);
        ObsbotCameraState? starting = null;
        ObsbotCameraState? ending = null;
        DateTimeOffset? firstChange = null;
        var success = false;
        var message = string.Empty;
        var lastPanVelocity = int.MinValue;
        var lastTiltVelocity = int.MinValue;
        var zoomApplied = false;
        _stopRequested = false;

        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await _gate.WaitAsync(cancellationToken);
                try
                {
                    if (_session is null)
                    {
                        message = "Camera is not connected.";
                        AddLog(DriverLogLevel.Warning, "AimAtSpeed", message);
                        break;
                    }

                    var current = _session.ReadState(true, null);
                    starting ??= current;
                    ending = current;
                    State = current;
                    PublishState();

                    if (!zoomApplied && target.Zoom is int zoom)
                    {
                        _session.SetCameraValues([KeyValuePair.Create(CameraControlKind.Zoom, zoom)]);
                        zoomApplied = true;
                    }

                    if (firstChange is null && starting is not null && HasChanged(starting, current))
                    {
                        firstChange = DateTimeOffset.UtcNow;
                    }

                    if (_stopRequested)
                    {
                        _session.StopRelativeMotion();
                        message = $"Stopped speed aim toward {target.Name}.";
                        AddLog(DriverLogLevel.Warning, "AimAtSpeed", message, target.ToString());
                        break;
                    }

                    if (DateTimeOffset.UtcNow >= deadline)
                    {
                        _session.StopRelativeMotion();
                        message = $"Timed out speed aim toward {target.Name}.";
                        AddLog(DriverLogLevel.Warning, "AimAtSpeed", message, target.ToString());
                        break;
                    }

                    var panVelocity = GetVelocity(current, CameraControlKind.Pan, target.Pan, normalizedPanSpeed, normalizedTolerance);
                    var tiltVelocity = GetVelocity(current, CameraControlKind.Tilt, target.Tilt, normalizedTiltSpeed, normalizedTolerance);
                    if (panVelocity == 0 && tiltVelocity == 0)
                    {
                        _session.StopRelativeMotion();
                        success = true;
                        message = $"Speed aimed at {target.Name} within +/-{normalizedTolerance}.";
                        AddLog(DriverLogLevel.Information, "AimAtSpeed", message, target.ToString());
                        break;
                    }

                    if (panVelocity != lastPanVelocity || tiltVelocity != lastTiltVelocity)
                    {
                        _session.SetRelativeVelocity(panVelocity, tiltVelocity);
                        lastPanVelocity = panVelocity;
                        lastTiltVelocity = tiltVelocity;
                        AddLog(DriverLogLevel.Trace, "AimAtSpeed", $"Velocity pan={panVelocity}, tilt={tiltVelocity}.", target.ToString());
                    }
                }
                finally
                {
                    _gate.Release();
                }

                await Task.Delay(20, cancellationToken);
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or ArgumentOutOfRangeException or OperationCanceledException)
        {
            message = ex is OperationCanceledException ? "Speed aim was cancelled." : ex.Message;
            await StopRelativeMotionSafelyAsync();
            AddLog(DriverLogLevel.Error, "AimAtSpeed", "Speed command failed.", message);
        }
        finally
        {
            await StopRelativeMotionSafelyAsync();
        }

        var result = new CameraCommandResult(
            commandId,
            "AimAtSpeed",
            ObsbotTransport.StandardUvc,
            target,
            sent,
            DateTimeOffset.UtcNow,
            firstChange,
            success,
            message,
            starting,
            ending);

        _commands.Insert(0, result);
        if (_commands.Count > _maxCommandEntries)
        {
            _commands.RemoveRange(_maxCommandEntries, _commands.Count - _maxCommandEntries);
        }

        CommandCompleted?.Invoke(result);
        return result;
    }

    public async Task<CameraCommandResult> SetZoomAsync(int zoom, CancellationToken cancellationToken = default)
    {
        var target = new AimTarget($"Zoom {zoom}", CurrentValue(CameraControlKind.Pan), CurrentValue(CameraControlKind.Tilt), zoom);
        return await AimAtAsync(target, cancellationToken);
    }

    public async Task<IReadOnlyList<CameraCommandResult>> RunRoutineAsync(IEnumerable<AimTarget> targets, TimeSpan dwell, CancellationToken cancellationToken = default)
    {
        _stopRequested = false;
        var results = new List<CameraCommandResult>();

        foreach (var target in targets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_stopRequested)
            {
                AddLog(DriverLogLevel.Warning, "Routine", "Routine stopped before remaining targets were executed.");
                break;
            }

            results.Add(await AimAtAsync(target, cancellationToken));
            if (dwell > TimeSpan.Zero)
            {
                await Task.Delay(dwell, cancellationToken);
            }
        }

        return results;
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        _stopRequested = true;
        await StopRelativeMotionSafelyAsync(cancellationToken);
        AddLog(DriverLogLevel.Warning, "Motion", "Stop requested. Relative motion was stopped; pending routines will stop before the next target.");
    }

    public async Task<ProtocolProbeReport> ProbeProtocolAsync(CancellationToken cancellationToken = default)
    {
        var state = await RefreshStateAsync(cancellationToken);
        var ranges = state.Controls.Values
            .Select(reading => reading.Range)
            .OfType<CameraControlRange>()
            .ToList();

        var captureToolStatus = new List<string>
        {
            ToolStatus("ffmpeg"),
            ToolStatus("Wireshark"),
            ToolStatus("tshark"),
            ToolStatus("USBPcapCMD")
        };

        var notes = new List<string>
        {
            "Standard UVC control probing is active through DirectShow IAMCameraControl.",
            "Absolute UVC pan/tilt/zoom has no standard speed argument; those Set calls request a target value only.",
            "UVC relative pan/tilt controls use signed values as speed and zero as stop. If supported, we can implement speed-controlled motion by issuing relative velocity, polling position, then stopping at target.",
            "Vendor extension-unit probing is represented as a discovery hook in v1; captured USB traffic can be imported for annotation.",
            "Raw WinUSB/libusb driver replacement is intentionally avoided so the standard webcam stream keeps working."
        };
        notes.AddRange(_session?.ProbeMotionSpeedSupport() ?? ["Motion speed probe skipped: camera is not connected."]);

        var report = new ProtocolProbeReport(
            DateTimeOffset.UtcNow,
            ranges,
            ["UVC extension-unit scan pending: use USBPcap/Wireshark capture import to identify candidate vendor commands."],
            captureToolStatus,
            notes);

        AddLog(DriverLogLevel.Information, "Probe", $"Protocol probe produced {ranges.Count} standard UVC control range(s).");
        return report;
    }

    public async Task<CaptureAnalysisSummary> AnalyzeCaptureAsync(string fileName, Stream capture, CancellationToken cancellationToken = default)
    {
        using var memory = new MemoryStream();
        await capture.CopyToAsync(memory, cancellationToken);
        var bytes = memory.ToArray();

        var setupLikePackets = 0;
        for (var index = 0; index + 7 < bytes.Length; index++)
        {
            var requestType = bytes[index];
            var request = bytes[index + 1];
            if ((requestType is 0x21 or 0xA1) && request is >= 0x01 and <= 0x0F)
            {
                setupLikePackets++;
            }
        }

        var summary = new CaptureAnalysisSummary(
            fileName,
            bytes.LongLength,
            setupLikePackets,
            [
                "This is a lightweight v1 scan for USB class/vendor control-transfer-like byte patterns.",
                "Use exported USBPcap/Wireshark/tshark captures while exercising OBSBOT Center to compare command timing and payloads."
            ]);

        AddLog(DriverLogLevel.Information, "Capture", $"Analyzed {fileName}: {bytes.LongLength} bytes, {setupLikePackets} candidate control-transfer pattern(s).");
        return summary;
    }

    public async Task<AimTarget> CaptureNamedTargetAsync(string name, CancellationToken cancellationToken = default)
    {
        var state = await RefreshStateAsync(cancellationToken);
        var target = new AimTarget(
            string.IsNullOrWhiteSpace(name) ? $"Target {_targets.Count + 1}" : name.Trim(),
            GetStateValue(state, CameraControlKind.Pan),
            GetStateValue(state, CameraControlKind.Tilt),
            GetStateValue(state, CameraControlKind.Zoom));

        _targets.RemoveAll(existing => string.Equals(existing.Name, target.Name, StringComparison.OrdinalIgnoreCase));
        _targets.Add(target);
        AddLog(DriverLogLevel.Information, "Target", $"Captured named target '{target.Name}'.");
        return target;
    }

    public async Task<CalibrationSample> RecordCalibrationSampleAsync(string name, double imageX, double imageY, CancellationToken cancellationToken = default)
    {
        var state = await RefreshStateAsync(cancellationToken);
        var pan = GetStateValue(state, CameraControlKind.Pan) ?? 0;
        var tilt = GetStateValue(state, CameraControlKind.Tilt) ?? 0;
        var sample = new CalibrationSample(
            string.IsNullOrWhiteSpace(name) ? $"Sample {_calibration.Count + 1}" : name.Trim(),
            imageX,
            imageY,
            pan,
            tilt,
            DateTimeOffset.UtcNow);

        _calibration.Add(sample);
        AddLog(DriverLogLevel.Information, "Calibration", $"Recorded sample '{sample.Name}' at image {imageX:0.000}, {imageY:0.000} -> pan {pan}, tilt {tilt}.");
        return sample;
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync();
        try
        {
            await DisconnectCoreAsync();
        }
        finally
        {
            _gate.Release();
        }

        _gate.Dispose();
    }

    private async Task<CameraCommandResult> ExecuteCommandAsync(
        string commandName,
        AimTarget? target,
        Func<DirectShowCameraSession, string> command,
        CancellationToken cancellationToken)
    {
        var commandId = Guid.NewGuid();
        var sent = DateTimeOffset.UtcNow;
        ObsbotCameraState? starting = null;
        ObsbotCameraState? ending = null;
        var success = false;
        var message = string.Empty;
        DateTimeOffset? firstChange = null;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_session is null)
            {
                message = "Camera is not connected.";
                AddLog(DriverLogLevel.Warning, commandName, message);
            }
            else
            {
                starting = State.IsConnected ? State : _session.ReadState(true, null);
                message = command(_session);
                ending = ApplyTargetToState(starting, target) ?? _session.ReadState(true, null);
                firstChange = HasChanged(starting, ending) ? DateTimeOffset.UtcNow : null;
                State = ending;
                success = true;
                AddLog(DriverLogLevel.Information, commandName, message, target?.ToString());
                PublishState();
            }
        }
        catch (Exception ex) when (ex is COMException or ArgumentOutOfRangeException or InvalidOperationException)
        {
            message = ex.Message;
            ending = _session?.ReadState(true, ex.Message);
            if (ending is not null)
            {
                State = ending;
                PublishState();
            }
            AddLog(DriverLogLevel.Error, commandName, "Command failed.", ex.Message);
        }
        finally
        {
            _gate.Release();
        }

        var result = new CameraCommandResult(
            commandId,
            commandName,
            ObsbotTransport.StandardUvc,
            target,
            sent,
            DateTimeOffset.UtcNow,
            firstChange,
            success,
            message,
            starting,
            ending);

        _commands.Insert(0, result);
        if (_commands.Count > _maxCommandEntries)
        {
            _commands.RemoveRange(_maxCommandEntries, _commands.Count - _maxCommandEntries);
        }

        CommandCompleted?.Invoke(result);
        return result;
    }

    private void StartPolling()
    {
        _pollingCts?.Cancel();
        _pollingCts?.Dispose();
        _pollingCts = new CancellationTokenSource();
        _pollingTask = Task.Run(() => PollAsync(_pollingCts.Token));
    }

    private async Task PollAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(PollInterval);
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!await _gate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            {
                continue;
            }

            try
            {
                if (_session is null)
                {
                    continue;
                }

                State = _session.ReadState(true, null);
                PublishState();
            }
            catch (Exception ex) when (ex is COMException or ObjectDisposedException)
            {
                State = State with { LastUpdatedUtc = DateTimeOffset.UtcNow, LastError = ex.Message };
                PublishState();
                AddLog(DriverLogLevel.Warning, "Polling", "Polling failed.", ex.Message);
            }
            finally
            {
                _gate.Release();
            }
        }
    }

    private async Task DisconnectCoreAsync()
    {
        _pollingCts?.Cancel();
        if (_pollingTask is not null)
        {
            try
            {
                await _pollingTask;
            }
            catch (OperationCanceledException)
            {
            }
        }

        _pollingCts?.Dispose();
        _pollingCts = null;
        _pollingTask = null;
        _session?.Dispose();
        _session = null;
    }

    private void PublishState()
    {
        StateChanged?.Invoke(State);
    }

    private void AddLog(DriverLogLevel level, string source, string message, string? raw = null)
    {
        var entry = new DriverLogEntry(Guid.NewGuid(), DateTimeOffset.UtcNow, level, source, message, raw);
        _logs.Insert(0, entry);
        if (_logs.Count > _maxLogEntries)
        {
            _logs.RemoveRange(_maxLogEntries, _logs.Count - _maxLogEntries);
        }

        LogAdded?.Invoke(entry);
    }

    private int? CurrentValue(CameraControlKind kind) => GetStateValue(State, kind);

    private static int? TryAddDelta(ObsbotCameraState state, CameraControlKind kind, int delta)
    {
        var current = GetStateValue(state, kind);
        if (current is null)
        {
            return null;
        }

        var next = current.Value + delta;
        if (state.Controls.TryGetValue(kind, out var reading) && reading.Range is { } range)
        {
            next = Math.Clamp(next, range.Min, range.Max);
        }

        return next;
    }

    private static int? GetStateValue(ObsbotCameraState state, CameraControlKind kind)
    {
        return state.Controls.TryGetValue(kind, out var reading) ? reading.Value : null;
    }

    private static bool HasChanged(ObsbotCameraState starting, ObsbotCameraState ending)
    {
        return CameraControlMap.Keys.Any(kind => GetStateValue(starting, kind) != GetStateValue(ending, kind));
    }

    private static ObsbotCameraState? ApplyTargetToState(ObsbotCameraState starting, AimTarget? target)
    {
        if (target is null || !starting.IsConnected)
        {
            return null;
        }

        var controls = starting.Controls.ToDictionary(pair => pair.Key, pair => pair.Value);
        Apply(CameraControlKind.Pan, target.Pan);
        Apply(CameraControlKind.Tilt, target.Tilt);
        Apply(CameraControlKind.Zoom, target.Zoom);

        return starting with
        {
            Controls = controls,
            LastUpdatedUtc = DateTimeOffset.UtcNow,
            LastError = null,
            IsPolling = false
        };

        void Apply(CameraControlKind kind, int? value)
        {
            if (value is not int requested || !controls.TryGetValue(kind, out var reading))
            {
                return;
            }

            var normalized = reading.Range is { } range
                ? Math.Clamp(requested, range.Min, range.Max)
                : requested;
            controls[kind] = reading with { Value = normalized, Error = null };
        }
    }

    private async Task StopRelativeMotionSafelyAsync(CancellationToken cancellationToken = default)
    {
        if (!await _gate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            _session?.StopRelativeMotion();
        }
        catch (COMException ex)
        {
            AddLog(DriverLogLevel.Warning, "Motion", "Relative stop failed.", ex.Message);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static int GetVelocity(ObsbotCameraState state, CameraControlKind kind, int? target, int speed, int tolerance)
    {
        if (target is not int requested)
        {
            return 0;
        }

        var current = GetStateValue(state, kind);
        if (current is null)
        {
            return 0;
        }

        var remaining = requested - current.Value;
        if (Math.Abs(remaining) <= tolerance)
        {
            return 0;
        }

        return Math.Sign(remaining) * speed;
    }

    private static string ToolStatus(string command)
    {
        var paths = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        var extensions = OperatingSystem.IsWindows()
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.BAT;.CMD").Split(';', StringSplitOptions.RemoveEmptyEntries)
            : [string.Empty];

        foreach (var path in paths)
        {
            foreach (var extension in extensions)
            {
                var candidate = Path.Combine(path, command.EndsWith(extension, StringComparison.OrdinalIgnoreCase) ? command : command + extension);
                if (File.Exists(candidate))
                {
                    return $"{command}: found at {candidate}";
                }
            }
        }

        return $"{command}: not found on PATH";
    }

    private static ObsbotDeviceInfo ToDeviceInfo(DsDevice device, int? deviceIndex = null)
    {
        var devicePath = device.DevicePath ?? string.Empty;
        var vid = Regex.Match(devicePath, "VID_([0-9A-F]{4})", RegexOptions.IgnoreCase).Groups[1].Value;
        var pid = Regex.Match(devicePath, "PID_([0-9A-F]{4})", RegexOptions.IgnoreCase).Groups[1].Value;
        var interfaceName = Regex.Match(devicePath, "MI_([0-9A-F]{2})", RegexOptions.IgnoreCase).Groups[1].Value;

        return new ObsbotDeviceInfo(
            device.Name,
            devicePath,
            string.IsNullOrWhiteSpace(vid) ? null : vid.ToUpperInvariant(),
            string.IsNullOrWhiteSpace(pid) ? null : pid.ToUpperInvariant(),
            string.IsNullOrWhiteSpace(interfaceName) ? "video" : $"MI_{interfaceName.ToUpperInvariant()}",
            deviceIndex);
    }

    private static bool IsPreferredObsbot(ObsbotDeviceInfo device)
    {
        return string.Equals(device.VendorId, ObsbotVendorId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(device.ProductId, ObsbotProductId, StringComparison.OrdinalIgnoreCase)
            || device.DisplayName.Contains("OBSBOT", StringComparison.OrdinalIgnoreCase);
    }

    private sealed class DirectShowCameraSession : IDisposable
    {
        private readonly IBaseFilter _filter;
        private readonly IAMCameraControl? _cameraControl;
        private readonly IKsPropertySet? _propertySet;
        private readonly IKsControl? _ksControl;
        private readonly ObsbotDeviceInfo _device;
        private readonly Dictionary<CameraControlKind, CameraControlRange> _ranges = [];

        private DirectShowCameraSession(IBaseFilter filter, IAMCameraControl? cameraControl, IKsPropertySet? propertySet, IKsControl? ksControl, ObsbotDeviceInfo device)
        {
            _filter = filter;
            _cameraControl = cameraControl;
            _propertySet = propertySet;
            _ksControl = ksControl;
            _device = device;
        }

        public static DirectShowCameraSession Open(DsDevice device)
        {
            var iid = typeof(IBaseFilter).GUID;
            device.Mon.BindToObject(null!, null!, ref iid, out var source);
            var filter = (IBaseFilter)source;
            var cameraControl = filter as IAMCameraControl;
            var propertySet = filter as IKsPropertySet;
            var ksControl = filter as IKsControl;
            return new DirectShowCameraSession(filter, cameraControl, propertySet, ksControl, ToDeviceInfo(device));
        }

        public ObsbotCameraState ReadState(bool isPolling, string? error)
        {
            var controls = new Dictionary<CameraControlKind, CameraControlReading>();
            foreach (var pair in CameraControlMap)
            {
                controls[pair.Key] = ReadCameraControl(pair.Key, pair.Value);
            }

            return new ObsbotCameraState(
                true,
                _device,
                controls,
                DateTimeOffset.UtcNow,
                error,
                isPolling);
        }

        public void SetCameraValues(IEnumerable<KeyValuePair<CameraControlKind, int>> values)
        {
            foreach (var pair in values)
            {
                SetCameraValue(pair.Key, pair.Value);
            }
        }

        public void SetRelativeVelocity(int panSpeed, int tiltSpeed)
        {
            if (_cameraControl is null)
            {
                throw new InvalidOperationException("Selected device does not expose IAMCameraControl.");
            }

            SetRawCameraValue((CameraControlProperty)CameraControlPanRelative, panSpeed);
            SetRawCameraValue((CameraControlProperty)CameraControlTiltRelative, tiltSpeed);
        }

        public void StopRelativeMotion()
        {
            if (_cameraControl is null)
            {
                return;
            }

            SetRawCameraValue((CameraControlProperty)CameraControlPanRelative, 0);
            SetRawCameraValue((CameraControlProperty)CameraControlTiltRelative, 0);
        }

        public void SetKsCameraControl(int propertyId, int value1, int? value2, int flags, KsCameraControlPayload payload)
        {
            if (payload is KsCameraControlPayload.PropertyValueFlagsCapabilities or KsCameraControlPayload.PropertyValue1FlagsCapabilitiesValue2)
            {
                SetKsControlCameraControl(propertyId, value1, value2, flags, payload);
                return;
            }

            if (_propertySet is null)
            {
                throw new InvalidOperationException("Selected device does not expose IKsPropertySet.");
            }

            var data = CreateKsPayload(propertyId, payload, value1, value2, flags);
            var dataPtr = Marshal.AllocHGlobal(data.Length);
            try
            {
                Marshal.Copy(data, 0, dataPtr, data.Length);
                var set = PropsetIdVidcapCameraControl;
                var hr = _propertySet.Set(ref set, propertyId, IntPtr.Zero, 0, dataPtr, data.Length);
                DsError.ThrowExceptionForHR(hr);
            }
            finally
            {
                Marshal.FreeHGlobal(dataPtr);
            }
        }

        private void SetKsControlCameraControl(int propertyId, int value1, int? value2, int flags, KsCameraControlPayload payload)
        {
            if (_ksControl is null)
            {
                throw new InvalidOperationException("Selected device does not expose IKsControl.");
            }

            var data = CreateKsPayload(propertyId, payload, value1, value2, flags);
            var dataPtr = Marshal.AllocHGlobal(data.Length);
            try
            {
                Marshal.Copy(data, 0, dataPtr, data.Length);
                var hr = _ksControl.KsProperty(dataPtr, data.Length, dataPtr, data.Length, out _);
                DsError.ThrowExceptionForHR(hr);
            }
            finally
            {
                Marshal.FreeHGlobal(dataPtr);
            }
        }

        private void SetCameraValue(CameraControlKind kind, int value)
        {
            if (_cameraControl is null)
            {
                throw new InvalidOperationException("Selected device does not expose IAMCameraControl.");
            }

            var property = CameraControlMap[kind];
            var range = GetOrProbeRange(kind, property, out var rangeError);
            if (range is not { IsSupported: true })
            {
                throw new InvalidOperationException(rangeError ?? $"{kind} is not supported by this camera.");
            }

            var normalized = Math.Clamp(value, range.Min, range.Max);
            var hr = _cameraControl.Set(property, normalized, CameraControlFlags.Manual);
            DsError.ThrowExceptionForHR(hr);
        }

        private void SetRawCameraValue(CameraControlProperty property, int value)
        {
            var hr = _cameraControl!.Set(property, value, CameraControlFlags.Manual);
            DsError.ThrowExceptionForHR(hr);
        }

        private static byte[] CreateKsPayload(int propertyId, KsCameraControlPayload payload, int value1, int? value2, int flags)
        {
            return payload switch
            {
                KsCameraControlPayload.ValueOnly => WriteInts(value1),
                KsCameraControlPayload.Value1Value2Only => WriteInts(value1, value2 ?? 0),
                KsCameraControlPayload.ValueFlagsCapabilities => WriteInts(value1, flags, 0),
                KsCameraControlPayload.Value1FlagsCapabilitiesValue2 => WriteInts(value1, flags, 0, value2 ?? 0),
                KsCameraControlPayload.Value1Value2FlagsCapabilities => WriteInts(value1, value2 ?? 0, flags, 0),
                KsCameraControlPayload.PropertyValueFlagsCapabilities => WriteKsProperty(propertyId, value1, flags, 0),
                KsCameraControlPayload.PropertyValue1FlagsCapabilitiesValue2 => WriteKsProperty(propertyId, value1, flags, 0, value2 ?? 0),
                _ => throw new ArgumentOutOfRangeException(nameof(payload), payload, null)
            };
        }

        private static byte[] WriteKsProperty(int propertyId, params int[] values)
        {
            const int ksPropertyTypeSet = 0x00000002;
            var bytes = new byte[24 + values.Length * sizeof(int)];
            PropsetIdVidcapCameraControl.TryWriteBytes(bytes.AsSpan(0, 16));
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(16, sizeof(int)), propertyId);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(20, sizeof(int)), ksPropertyTypeSet);
            for (var i = 0; i < values.Length; i++)
            {
                BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(24 + i * sizeof(int), sizeof(int)), values[i]);
            }

            return bytes;
        }

        private static byte[] WriteInts(params int[] values)
        {
            var bytes = new byte[values.Length * sizeof(int)];
            for (var i = 0; i < values.Length; i++)
            {
                BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(i * sizeof(int), sizeof(int)), values[i]);
            }

            return bytes;
        }

        public void Dispose()
        {
            if (Marshal.IsComObject(_filter))
            {
                Marshal.ReleaseComObject(_filter);
            }
        }

        private CameraControlReading ReadCameraControl(CameraControlKind kind, CameraControlProperty property)
        {
            if (_cameraControl is null)
            {
                return new CameraControlReading(kind, null, null, null, "IAMCameraControl", "IAMCameraControl is not available.");
            }

            try
            {
                var range = GetOrProbeRange(kind, property, out var rangeError);
                if (range is null)
                {
                    return new CameraControlReading(kind, null, null, null, "IAMCameraControl", rangeError);
                }

                var getHr = _cameraControl.Get(property, out var value, out var flags);
                if (getHr < 0)
                {
                    return new CameraControlReading(kind, null, null, range, "IAMCameraControl", DsError.GetErrorText(getHr));
                }

                return new CameraControlReading(kind, value, (int)flags, range, "IAMCameraControl");
            }
            catch (COMException ex)
            {
                return new CameraControlReading(kind, null, null, null, "IAMCameraControl", ex.Message);
            }
        }

        private CameraControlRange? GetOrProbeRange(CameraControlKind kind, CameraControlProperty property, out string? error)
        {
            error = null;
            if (_ranges.TryGetValue(kind, out var cached))
            {
                return cached;
            }

            if (_cameraControl is null)
            {
                error = "IAMCameraControl is not available.";
                return null;
            }

            var rangeHr = _cameraControl.GetRange(property, out var min, out var max, out var step, out var defaultValue, out var caps);
            if (rangeHr < 0)
            {
                error = DsError.GetErrorText(rangeHr);
                return null;
            }

            var range = new CameraControlRange(kind, min, max, step, defaultValue, (int)caps, true, "IAMCameraControl");
            _ranges[kind] = range;
            return range;
        }

        public IReadOnlyList<string> ProbeMotionSpeedSupport()
        {
            if (_propertySet is null)
            {
                return ["Motion speed probe: DirectShow filter did not expose IKsPropertySet."];
            }

            return
            [
                ProbeCameraControlProperty("PAN_RELATIVE", 10),
                ProbeCameraControlProperty("TILT_RELATIVE", 11),
                ProbeCameraControlProperty("PANTILT_RELATIVE", 17)
            ];
        }

        private string ProbeCameraControlProperty(string name, int id)
        {
            try
            {
                var set = PropsetIdVidcapCameraControl;
                var hr = _propertySet!.QuerySupported(ref set, id, out var support);
                if (hr < 0)
                {
                    return $"Motion speed probe: {name} unsupported ({DsError.GetErrorText(hr)}).";
                }

                var canGet = (support & KsPropertySupportGet) != 0;
                var canSet = (support & KsPropertySupportSet) != 0;
                return $"Motion speed probe: {name} support=0x{support:X}; get={canGet}; set={canSet}.";
            }
            catch (COMException ex)
            {
                return $"Motion speed probe: {name} failed ({ex.Message}).";
            }
        }
    }

    [ComImport]
    [Guid("31EFAC30-515C-11D0-A9AA-00AA0061BE93")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IKsPropertySet
    {
        [PreserveSig]
        int Set(
            [In] ref Guid propSet,
            [In] int id,
            [In] IntPtr instanceData,
            [In] int instanceLength,
            [In] IntPtr propertyData,
            [In] int dataLength);

        [PreserveSig]
        int Get(
            [In] ref Guid propSet,
            [In] int id,
            [In] IntPtr instanceData,
            [In] int instanceLength,
            [Out] IntPtr propertyData,
            [In] int dataLength,
            out int bytesReturned);

        [PreserveSig]
        int QuerySupported([In] ref Guid propSet, [In] int id, out int typeSupport);
    }

    [ComImport]
    [Guid("28F54685-06FD-11D2-B27A-00A0C9223196")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IKsControl
    {
        [PreserveSig]
        int KsProperty(
            [In] IntPtr property,
            [In] int propertyLength,
            [In, Out] IntPtr propertyData,
            [In] int dataLength,
            out int bytesReturned);

        [PreserveSig]
        int KsMethod(
            [In] IntPtr method,
            [In] int methodLength,
            [In, Out] IntPtr methodData,
            [In] int dataLength,
            out int bytesReturned);

        [PreserveSig]
        int KsEvent(
            [In] IntPtr @event,
            [In] int eventLength,
            [In, Out] IntPtr eventData,
            [In] int dataLength,
            out int bytesReturned);
    }
}
