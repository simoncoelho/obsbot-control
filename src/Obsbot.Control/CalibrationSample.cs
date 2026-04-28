namespace Obsbot.Control;

public sealed record CalibrationSample(
    string Name,
    double ImageX,
    double ImageY,
    int Pan,
    int Tilt,
    DateTimeOffset CapturedAtUtc);
