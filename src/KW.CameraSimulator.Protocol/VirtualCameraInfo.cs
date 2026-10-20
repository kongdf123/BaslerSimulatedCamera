namespace KW.CameraSimulator.Protocol;

public class VirtualCameraInfo
{
    public string SerialNumber { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public int Width { get; set; }
    public int Height { get; set; }
    public int Fps { get; set; }
    public string Vendor { get; set; } = "RemoteSimulated";
    public string Driver { get; set; } = "RemoteSimulated";

    public static VirtualCameraInfo[] DefaultCameras => new[]
    {
        new VirtualCameraInfo { SerialNumber = "SIM-CAM-01", DisplayName = "Simulated Camera 1", Width = 640, Height = 480, Fps = 100 },
        new VirtualCameraInfo { SerialNumber = "SIM-CAM-02", DisplayName = "Simulated Camera 2", Width = 640, Height = 480, Fps = 100 },
        new VirtualCameraInfo { SerialNumber = "SIM-CAM-03", DisplayName = "Simulated Camera 3", Width = 640, Height = 480, Fps = 100 },
        new VirtualCameraInfo { SerialNumber = "SIM-CAM-04", DisplayName = "Simulated Camera 4", Width = 640, Height = 480, Fps = 100 },
    };
}