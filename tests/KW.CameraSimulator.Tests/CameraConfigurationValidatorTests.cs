using KW.CameraSimulator.Protocol;
using KW.CameraSimulatorTool.Services;

namespace KW.CameraSimulator.Tests;

public class CameraConfigurationValidatorTests
{
    private static CameraConfiguration Valid => new(
        640, 480, 100,
        SimulatedPixelFormat.Bgr24,
        TestPatterns.ColorBars);

    [Fact]
    public void AcceptsValidConfiguration()
    {
        Assert.Null(CameraConfigurationValidator.Validate(Valid));
    }

    [Fact]
    public void AcceptsBoundaryMin()
    {
        var cfg = Valid with { Width = 64, Height = 64, TargetFrameRate = 1 };
        Assert.Null(CameraConfigurationValidator.Validate(cfg));
    }

    [Fact]
    public void AcceptsBoundaryMax()
    {
        var cfg = Valid with { Width = 1920, Height = 1080, TargetFrameRate = 240 };
        Assert.Null(CameraConfigurationValidator.Validate(cfg));
    }

    [Fact]
    public void RejectsWidthZero()
    {
        var cfg = Valid with { Width = 0 };
        Assert.NotNull(CameraConfigurationValidator.Validate(cfg));
    }

    [Fact]
    public void RejectsWidthAboveMax()
    {
        var cfg = Valid with { Width = 1921 };
        Assert.NotNull(CameraConfigurationValidator.Validate(cfg));
    }

    [Fact]
    public void RejectsHeightZero()
    {
        var cfg = Valid with { Height = 0 };
        Assert.NotNull(CameraConfigurationValidator.Validate(cfg));
    }

    [Fact]
    public void RejectsHeightAboveMax()
    {
        var cfg = Valid with { Height = 1081 };
        Assert.NotNull(CameraConfigurationValidator.Validate(cfg));
    }

    [Fact]
    public void RejectsFpsBelowOne()
    {
        var cfg = Valid with { TargetFrameRate = 0.5 };
        Assert.NotNull(CameraConfigurationValidator.Validate(cfg));
    }

    [Fact]
    public void RejectsFpsAboveMax()
    {
        var cfg = Valid with { TargetFrameRate = 241 };
        Assert.NotNull(CameraConfigurationValidator.Validate(cfg));
    }

    [Fact]
    public void RejectsNaN()
    {
        var cfg = Valid with { TargetFrameRate = double.NaN };
        Assert.NotNull(CameraConfigurationValidator.Validate(cfg));
    }

    [Fact]
    public void RejectsInfinity()
    {
        var cfg = Valid with { TargetFrameRate = double.PositiveInfinity };
        Assert.NotNull(CameraConfigurationValidator.Validate(cfg));
    }

    [Fact]
    public void RejectsUnknownPattern()
    {
        var cfg = Valid with { TestPattern = "UnknownPattern" };
        Assert.NotNull(CameraConfigurationValidator.Validate(cfg));
    }

    [Fact]
    public void RejectsEmptyPattern()
    {
        var cfg = Valid with { TestPattern = string.Empty };
        Assert.NotNull(CameraConfigurationValidator.Validate(cfg));
    }

    [Fact]
    public void RejectsInvalidWidthReturnsMeaningfulMessage()
    {
        var cfg = Valid with { Width = 10 };
        string? err = CameraConfigurationValidator.Validate(cfg);
        Assert.NotNull(err);
        Assert.Contains("Width", err);
        Assert.Contains("64", err);
        Assert.Contains("1920", err);
    }

    [Fact]
    public void AllFourPatternsAreAccepted()
    {
        foreach (var pattern in TestPatterns.All)
        {
            var cfg = Valid with { TestPattern = pattern };
            Assert.Null(CameraConfigurationValidator.Validate(cfg));
        }
    }

    [Fact]
    public void GetCapabilitiesReturnsExpectedBounds()
    {
        var caps = CameraConfigurationValidator.GetCapabilities();
        Assert.Equal(64, caps.MinWidth);
        Assert.Equal(1920, caps.MaxWidth);
        Assert.Equal(64, caps.MinHeight);
        Assert.Equal(1080, caps.MaxHeight);
        Assert.Equal(1, caps.MinFrameRate, 0.01);
        Assert.Equal(240, caps.MaxFrameRate, 0.01);
        Assert.Equal(4, caps.SupportedTestPatterns.Count);
        Assert.Contains("ColorBars", caps.SupportedTestPatterns);
        Assert.Contains("Gradient", caps.SupportedTestPatterns);
        Assert.Contains("Checkerboard", caps.SupportedTestPatterns);
        Assert.Contains("MovingTarget", caps.SupportedTestPatterns);
        Assert.Single(caps.SupportedPixelFormats);
    }
}