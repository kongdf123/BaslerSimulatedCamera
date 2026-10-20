namespace KW.Capture.Camera;

public static class CameraDriverVendorResolver
{
    public static CameraDriver? TryResolve(string? vendor)
    {
        if (string.IsNullOrWhiteSpace(vendor))
            return null;

        string normalized = vendor.Trim();

        // 注意：RemoteSimulated 必须在 Simulated 之前判断，否则 "RemoteSimulated" 会被 "Simulated" 提前匹配。
        if (normalized.Contains("RemoteSimulated", StringComparison.OrdinalIgnoreCase))
        {
            return CameraDriver.RemoteSimulated;
        }

        if (normalized.Contains("Basler", StringComparison.OrdinalIgnoreCase))
        {
            return CameraDriver.Basler;
        }

        if (normalized.Contains("Hik", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("Hikvision", StringComparison.OrdinalIgnoreCase))
        {
            return CameraDriver.Hik;
        }

        if (normalized.Contains("Huaray", StringComparison.OrdinalIgnoreCase))
        {
            return CameraDriver.Huaray;
        }

        if (normalized.Contains("ZCam", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("Z-CAM", StringComparison.OrdinalIgnoreCase))
        {
            return CameraDriver.ZCam;
        }

        if (normalized.Contains("Simulated", StringComparison.OrdinalIgnoreCase))
        {
            return CameraDriver.Simulated;
        }

        if (normalized.Contains("Usb", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("UVC", StringComparison.OrdinalIgnoreCase))
        {
            return CameraDriver.Usb;
        }

        return null;
    }
}