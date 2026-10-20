using System.Text.Json.Serialization;
using KW.CameraSimulator.Protocol;

namespace KW.CameraSimulatorTool.Models;

public class VirtualCameraState
{
    public string SerialNumber { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public int Width { get; set; } = 640;
    public int Height { get; set; } = 480;
    public int Fps { get; set; } = 100;
    public bool Enabled { get; set; } = true;
    public bool IsStreaming { get; set; }
    public long FramesSent { get; set; }
    public double ActualFps { get; set; }

    [JsonIgnore]
    public FrameGenerator? Generator { get; set; }

    public VirtualCameraInfo ToInfo()
    {
        return new VirtualCameraInfo
        {
            SerialNumber = SerialNumber,
            DisplayName = DisplayName,
            Width = Width,
            Height = Height,
            Fps = Fps,
        };
    }
}