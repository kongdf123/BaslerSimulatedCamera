using KW.CameraSimulator.Protocol;

namespace KW.CameraSimulatorTool.Services;

public static class CameraConfigurationValidator
{
    public const int MinWidth = 64;
    public const int MaxWidth = 1920;
    public const int MinHeight = 64;
    public const int MaxHeight = 1080;
    public const double MinFrameRate = 1;
    public const double MaxFrameRate = 240;

    private static readonly HashSet<string> Patterns =
        new(TestPatterns.All, StringComparer.OrdinalIgnoreCase);

    public static string? Validate(CameraConfiguration config)
    {
        if (config.Width is < MinWidth or > MaxWidth)
            return $"Width must be between {MinWidth} and {MaxWidth}.";

        if (config.Height is < MinHeight or > MaxHeight)
            return $"Height must be between {MinHeight} and {MaxHeight}.";

        if (config.TargetFrameRate is < MinFrameRate or > MaxFrameRate ||
            double.IsNaN(config.TargetFrameRate) ||
            double.IsInfinity(config.TargetFrameRate))
        {
            return $"Target FPS must be between {MinFrameRate} and {MaxFrameRate}.";
        }

        if (!Enum.IsDefined(config.PixelFormat))
            return "Unsupported pixel format.";

        if (!Patterns.Contains(config.TestPattern))
            return "Unsupported test pattern.";

        return null;
    }

    public static CameraConfigurationCapabilities GetCapabilities()
    {
        return new CameraConfigurationCapabilities(
            MinWidth: MinWidth,
            MaxWidth: MaxWidth,
            WidthStep: 1,
            MinHeight: MinHeight,
            MaxHeight: MaxHeight,
            HeightStep: 1,
            MinFrameRate: MinFrameRate,
            MaxFrameRate: MaxFrameRate,
            SupportedTestPatterns: TestPatterns.All,
            SupportedPixelFormats: new[] { SimulatedPixelFormat.Bgr24 });
    }
}