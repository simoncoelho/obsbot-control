using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.Options;
using Obsbot.Control;
using OpenCvSharp;

namespace obsbot_control.Services;

public sealed class FfmpegCameraStreamService(
    IOptions<FfmpegCameraStreamOptions> options,
    IObsbotCameraDriver driver,
    ILogger<FfmpegCameraStreamService> logger)
{
    private readonly FfmpegCameraStreamOptions _options = options.Value;
    private readonly object _streamLock = new();
    private Process? _activeFfmpegProcess;

    public async Task StreamMjpegAsync(HttpResponse response, string? backend, string? orientation, CancellationToken cancellationToken)
    {
        if (!ShouldUseFfmpeg(backend))
        {
            await StreamOpenCvMjpegAsync(response, orientation, cancellationToken);
            return;
        }

        await StreamFfmpegMjpegAsync(response, orientation, cancellationToken);
    }

    public async Task StreamMjpegAsync(HttpResponse response, CancellationToken cancellationToken)
    {
        await StreamMjpegAsync(response, null, null, cancellationToken);
    }

    private async Task StreamFfmpegMjpegAsync(HttpResponse response, string? orientation, CancellationToken cancellationToken)
    {
        response.ContentType = $"multipart/x-mixed-replace; boundary={_options.Boundary}";
        response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
        response.Headers.Pragma = "no-cache";

        using var process = CreateProcess(orientation);
        try
        {
            lock (_streamLock)
            {
                if (_activeFfmpegProcess is not null && !_activeFfmpegProcess.HasExited)
                {
                    TryKill(_activeFfmpegProcess);
                }

                _activeFfmpegProcess = process;
            }

            process.Start();
        }
        catch (Win32Exception ex)
        {
            response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            await response.WriteAsync($"ffmpeg could not be started: {ex.Message}", cancellationToken);
            return;
        }

        var errorPump = PumpStandardErrorAsync(process, cancellationToken);

        try
        {
            await PumpJpegPipeAsMjpegAsync(process.StandardOutput.BaseStream, response, cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException ex)
        {
            logger.LogDebug(ex, "MJPEG stream ended.");
        }
        finally
        {
            TryKill(process);
            lock (_streamLock)
            {
                if (ReferenceEquals(_activeFfmpegProcess, process))
                {
                    _activeFfmpegProcess = null;
                }
            }

            await errorPump;
        }
    }

    public async Task StreamOpenCvMjpegAsync(HttpResponse response, string? orientation, CancellationToken cancellationToken)
    {
        response.ContentType = $"multipart/x-mixed-replace; boundary={_options.Boundary}";
        response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
        response.Headers.Pragma = "no-cache";

        var deviceIndex = await FindPreferredDeviceIndexAsync(cancellationToken);
        if (deviceIndex is null)
        {
            response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            await response.WriteAsync("No OBSBOT video device index was found for in-process capture.", cancellationToken);
            return;
        }

        using var capture = new VideoCapture(deviceIndex.Value, VideoCaptureAPIs.DSHOW);
        if (!capture.IsOpened())
        {
            response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            await response.WriteAsync($"OpenCV could not open OBSBOT camera index {deviceIndex.Value}.", cancellationToken);
            return;
        }

        ApplyCaptureSize(capture);
        var selectedOrientation = NormalizeOrientation(orientation);
        using var frame = new Mat();
        using var orientedFrame = new Mat();
        var delay = TimeSpan.FromMilliseconds(Math.Max(1, 1000 / Math.Max(1, _options.Framerate)));

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (!capture.Read(frame) || frame.Empty())
                {
                    await Task.Delay(delay, cancellationToken);
                    continue;
                }

                var output = ApplyOrientation(frame, orientedFrame, selectedOrientation);
                Cv2.ImEncode(".jpg", output, out var jpeg);
                await WriteMjpegFrameAsync(response, jpeg, cancellationToken);
                await Task.Delay(delay, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException ex)
        {
            logger.LogDebug(ex, "OpenCV MJPEG stream ended.");
        }
    }

    public string DescribeCommand()
    {
        var pixelFormat = string.IsNullOrWhiteSpace(_options.InputPixelFormat) ? string.Empty : $"-pixel_format {_options.InputPixelFormat} ";
        return $"{_options.FfmpegPath} -f dshow {pixelFormat}-video_size {_options.VideoSize} -framerate {_options.Framerate} -i video=\"{_options.VideoDeviceName}\" -an -f image2pipe -vcodec mjpeg -q:v {_options.Quality} pipe:1";
    }

    private async Task WriteMjpegFrameAsync(HttpResponse response, ReadOnlyMemory<byte> jpeg, CancellationToken cancellationToken)
    {
        await response.WriteAsync($"--{_options.Boundary}\r\n", cancellationToken);
        await response.WriteAsync("Content-Type: image/jpeg\r\n", cancellationToken);
        await response.WriteAsync($"Content-Length: {jpeg.Length}\r\n\r\n", cancellationToken);
        await response.Body.WriteAsync(jpeg, cancellationToken);
        await response.WriteAsync("\r\n", cancellationToken);
        await response.Body.FlushAsync(cancellationToken);
    }

    public string DescribeActiveStream()
    {
        return ShouldUseFfmpeg(null)
            ? DescribeCommand()
            : $"OpenCV DirectShow in-process MJPEG stream ({OrientationLabel(null)})";
    }

    public string ActiveBackendLabel => ShouldUseFfmpeg(null)
        ? $"FFmpeg DirectShow, {OrientationLabel(null)}"
        : $"OpenCV DirectShow, {OrientationLabel(null)}";

    public string ActiveOrientation => NormalizeOrientation(null);

    public string OrientationLabel(string? orientation)
    {
        return NormalizeOrientation(orientation) switch
        {
            "vertical-left" => "vertical left",
            "vertical-right" => "vertical right",
            _ => "horizontal"
        };
    }

    public bool ShouldUseFfmpeg(string? backend)
    {
        var selected = string.IsNullOrWhiteSpace(backend) ? _options.Backend : backend;
        return string.Equals(selected, "ffmpeg", StringComparison.OrdinalIgnoreCase) && IsFfmpegAvailable();
    }

    public bool IsFfmpegAvailable()
    {
        if (Path.IsPathRooted(_options.FfmpegPath))
        {
            return File.Exists(_options.FfmpegPath);
        }

        var paths = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        var extensions = OperatingSystem.IsWindows()
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.BAT;.CMD").Split(';', StringSplitOptions.RemoveEmptyEntries)
            : [string.Empty];

        return paths.Any(path => extensions.Any(extension =>
            File.Exists(Path.Combine(path, _options.FfmpegPath.EndsWith(extension, StringComparison.OrdinalIgnoreCase)
                ? _options.FfmpegPath
                : _options.FfmpegPath + extension))));
    }

    private Process CreateProcess(string? orientation)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _options.FfmpegPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        startInfo.ArgumentList.Add("-hide_banner");
        startInfo.ArgumentList.Add("-loglevel");
        startInfo.ArgumentList.Add("warning");
        startInfo.ArgumentList.Add("-f");
        startInfo.ArgumentList.Add("dshow");
        if (!string.IsNullOrWhiteSpace(_options.InputPixelFormat))
        {
            startInfo.ArgumentList.Add("-pixel_format");
            startInfo.ArgumentList.Add(_options.InputPixelFormat);
        }

        startInfo.ArgumentList.Add("-video_size");
        startInfo.ArgumentList.Add(_options.VideoSize);
        startInfo.ArgumentList.Add("-framerate");
        startInfo.ArgumentList.Add(_options.Framerate.ToString());
        startInfo.ArgumentList.Add("-i");
        startInfo.ArgumentList.Add($"video={_options.VideoDeviceName}");
        startInfo.ArgumentList.Add("-an");
        AddFfmpegVideoFilter(startInfo, orientation);
        startInfo.ArgumentList.Add("-f");
        startInfo.ArgumentList.Add("image2pipe");
        startInfo.ArgumentList.Add("-vcodec");
        startInfo.ArgumentList.Add("mjpeg");
        startInfo.ArgumentList.Add("-q:v");
        startInfo.ArgumentList.Add(_options.Quality.ToString());
        startInfo.ArgumentList.Add("pipe:1");

        return new Process { StartInfo = startInfo, EnableRaisingEvents = true };
    }

    private async Task PumpJpegPipeAsMjpegAsync(Stream source, HttpResponse response, CancellationToken cancellationToken)
    {
        var buffer = new byte[32 * 1024];
        using var frame = new MemoryStream(256 * 1024);
        var inFrame = false;
        var previous = -1;
        var framesWritten = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            var read = await source.ReadAsync(buffer, cancellationToken);
            if (read <= 0)
            {
                break;
            }

            for (var index = 0; index < read; index++)
            {
                var current = buffer[index];
                if (!inFrame)
                {
                    if (previous == 0xFF && current == 0xD8)
                    {
                        inFrame = true;
                        frame.SetLength(0);
                        frame.WriteByte(0xFF);
                        frame.WriteByte(0xD8);
                    }
                }
                else
                {
                    frame.WriteByte(current);
                    if (previous == 0xFF && current == 0xD9)
                    {
                        await WriteMjpegFrameAsync(response, frame.ToArray(), cancellationToken);
                        framesWritten++;
                        if (framesWritten == 1)
                        {
                            logger.LogInformation("FFmpeg stream wrote first JPEG frame ({Length} bytes).", frame.Length);
                        }

                        inFrame = false;
                        frame.SetLength(0);
                    }
                }

                previous = current;
            }
        }
    }

    private void AddFfmpegVideoFilter(ProcessStartInfo startInfo, string? orientation)
    {
        var selected = NormalizeOrientation(orientation);
        var filters = new List<string>();
        var orientationFilter = selected switch
        {
            "vertical-left" => "transpose=2",
            "vertical-right" => "transpose=1",
            _ => null
        };

        if (!string.IsNullOrWhiteSpace(orientationFilter))
        {
            filters.Add(orientationFilter);
        }

        if (string.Equals(_options.InputPixelFormat, "yuyv422", StringComparison.OrdinalIgnoreCase))
        {
            filters.Add("format=yuvj422p");
        }

        if (filters.Count > 0)
        {
            startInfo.ArgumentList.Add("-vf");
            startInfo.ArgumentList.Add(string.Join(',', filters));
        }
    }

    private string NormalizeOrientation(string? orientation)
    {
        var selected = string.IsNullOrWhiteSpace(orientation) ? _options.Orientation : orientation;
        return selected?.Trim().ToLowerInvariant() switch
        {
            "vertical" => "vertical-left",
            "portrait" => "vertical-left",
            "portrait-left" => "vertical-left",
            "vertical-left" => "vertical-left",
            "left" => "vertical-left",
            "portrait-right" => "vertical-right",
            "vertical-right" => "vertical-right",
            "right" => "vertical-right",
            _ => "horizontal"
        };
    }

    private static Mat ApplyOrientation(Mat frame, Mat scratch, string orientation)
    {
        switch (orientation)
        {
            case "vertical-left":
                Cv2.Rotate(frame, scratch, RotateFlags.Rotate90Counterclockwise);
                return scratch;
            case "vertical-right":
                Cv2.Rotate(frame, scratch, RotateFlags.Rotate90Clockwise);
                return scratch;
            default:
                return frame;
        }
    }

    private async Task<int?> FindPreferredDeviceIndexAsync(CancellationToken cancellationToken)
    {
        var devices = await driver.DiscoverDevicesAsync(cancellationToken);
        var preferred = devices.FirstOrDefault(device =>
            string.Equals(device.VendorId, "3564", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(device.ProductId, "FEFF", StringComparison.OrdinalIgnoreCase))
            ?? devices.FirstOrDefault(device => device.DisplayName.Contains("OBSBOT", StringComparison.OrdinalIgnoreCase));

        return preferred?.DeviceIndex;
    }

    private void ApplyCaptureSize(VideoCapture capture)
    {
        var parts = _options.VideoSize.Split('x', 'X');
        if (parts.Length == 2 &&
            int.TryParse(parts[0], out var width) &&
            int.TryParse(parts[1], out var height))
        {
            capture.Set(VideoCaptureProperties.FrameWidth, width);
            capture.Set(VideoCaptureProperties.FrameHeight, height);
        }

        capture.Set(VideoCaptureProperties.Fps, _options.Framerate);
    }

    private async Task PumpStandardErrorAsync(Process process, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await process.StandardError.ReadLineAsync(cancellationToken);
                if (line is null)
                {
                    break;
                }

                if (!string.IsNullOrWhiteSpace(line))
                {
                    logger.LogWarning("ffmpeg: {Line}", line);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
        catch (Win32Exception)
        {
        }
    }
}
