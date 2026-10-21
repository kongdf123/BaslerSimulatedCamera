namespace KW.CameraSimulator.Protocol;

public enum SimulatedPixelFormat
{
    Bgr24 = 0
}

public sealed record CameraConfiguration(
    int Width,
    int Height,
    double TargetFrameRate,
    SimulatedPixelFormat PixelFormat,
    string TestPattern);

public sealed record CameraConfigurationCapabilities(
    int MinWidth,
    int MaxWidth,
    int WidthStep,
    int MinHeight,
    int MaxHeight,
    int HeightStep,
    double MinFrameRate,
    double MaxFrameRate,
    IReadOnlyList<string> SupportedTestPatterns,
    IReadOnlyList<SimulatedPixelFormat> SupportedPixelFormats);

public static class TestPatterns
{
    public const string ColorBars = "ColorBars";
    public const string Gradient = "Gradient";
    public const string Checkerboard = "Checkerboard";
    public const string MovingTarget = "MovingTarget";

    public static IReadOnlyList<string> All { get; } = new[]
    {
        ColorBars, Gradient, Checkerboard, MovingTarget
    };
}