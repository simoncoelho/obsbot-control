namespace obsbot_control.Services;

public sealed class FfmpegCameraStreamOptions
{
    public string Backend { get; set; } = "ffmpeg";
    public string Orientation { get; set; } = "vertical-left";
    public string FfmpegPath { get; set; } = "ffmpeg";
    public string VideoDeviceName { get; set; } = "OBSBOT Tiny SE StreamCamera";
    public string VideoSize { get; set; } = "640x360";
    public string InputPixelFormat { get; set; } = "yuyv422";
    public int Framerate { get; set; } = 30;
    public int Quality { get; set; } = 5;
    public string Boundary { get; set; } = "ffmpeg";
}
