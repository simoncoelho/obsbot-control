namespace Obsbot.Control;

public sealed record ObsbotDeviceInfo(
    string DisplayName,
    string DevicePath,
    string? VendorId,
    string? ProductId,
    string InterfaceName,
    int? DeviceIndex = null);
