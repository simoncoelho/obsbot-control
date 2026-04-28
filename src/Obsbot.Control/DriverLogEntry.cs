namespace Obsbot.Control;

public sealed record DriverLogEntry(
    Guid Id,
    DateTimeOffset TimestampUtc,
    DriverLogLevel Level,
    string Source,
    string Message,
    string? Raw = null);
