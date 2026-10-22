using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using KW.CameraSimulator.Protocol;
using KW.CameraSimulatorTool.Services;

namespace KW.CameraSimulatorTool.Models;

public class VirtualCameraState : INotifyPropertyChanged
{
    private string _serialNumber = string.Empty;
    private string _displayName = string.Empty;
    private int _width = 640;
    private int _height = 480;
    private double _fps = 100;
    private string _testPattern = TestPatterns.ColorBars;
    private bool _enabled = true;
    private bool _isStreaming;
    private long _framesSent;
    private double _actualFps;
    private string _validationError = string.Empty;

    public string SerialNumber { get => _serialNumber; set { _serialNumber = value; OnPropertyChanged(); } }
    public string DisplayName { get => _displayName; set { _displayName = value; OnPropertyChanged(); } }

    public int Width
    {
        get => _width;
        set { if (_width != value) { _width = value; OnPropertyChanged(); Validate(); } }
    }

    public int Height
    {
        get => _height;
        set { if (_height != value) { _height = value; OnPropertyChanged(); Validate(); } }
    }

    public double Fps
    {
        get => _fps;
        set { if (_fps != value) { _fps = value; OnPropertyChanged(); Validate(); } }
    }

    public string TestPattern
    {
        get => _testPattern;
        set { if (_testPattern != value) { _testPattern = value; OnPropertyChanged(); Validate(); } }
    }

    public bool Enabled { get => _enabled; set { _enabled = value; OnPropertyChanged(); } }
    public bool IsStreaming { get => _isStreaming; set { _isStreaming = value; OnPropertyChanged(); } }
    public long FramesSent { get => _framesSent; set { _framesSent = value; OnPropertyChanged(); } }
    public double ActualFps { get => _actualFps; set { _actualFps = value; OnPropertyChanged(); } }

    public string ValidationError
    {
        get => _validationError;
        private set { _validationError = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasValidationError)); }
    }

    public bool HasValidationError => !string.IsNullOrEmpty(ValidationError);

    [JsonIgnore]
    public FrameGenerator? Generator { get; set; }

    public object SyncRoot { get; } = new();

    public void Validate()
    {
        var cfg = new CameraConfiguration(Width, Height, Fps, SimulatedPixelFormat.Bgr24, TestPattern);
        ValidationError = CameraConfigurationValidator.Validate(cfg) ?? string.Empty;
    }

    public void EnsureGenerator(int slotIndex)
    {
        if (Generator == null ||
            Generator.Width != Width ||
            Generator.Height != Height ||
            Generator.TestPattern != TestPattern)
        {
            Generator = new FrameGenerator(Width, Height, slotIndex, TestPattern);
        }
    }

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

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}