using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Threading;
using KW.CameraSimulator.Protocol;
using KW.CameraSimulatorTool.Models;
using KW.CameraSimulatorTool.Services;

namespace KW.CameraSimulatorTool.ViewModels;

public class SimulatorViewModel : INotifyPropertyChanged
{
    private CameraSimulatorServer? _server;
    private string _serverStatus = "Stopped";
    private bool _isServerRunning;
    private int _port = CameraProtocol.DefaultPort;
    private string _log = string.Empty;

    public ObservableCollection<VirtualCameraState> Cameras { get; } = new();
    public ObservableCollection<string> Logs { get; } = new();

    public int Port
    {
        get => _port;
        set { _port = value; OnPropertyChanged(); }
    }

    public string ServerStatus
    {
        get => _serverStatus;
        set { _serverStatus = value; OnPropertyChanged(); }
    }

    public bool IsServerRunning
    {
        get => _isServerRunning;
        set { _isServerRunning = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsServerStopped)); }
    }

    public bool IsServerStopped => !_isServerRunning;

    public SimulatorViewModel()
    {
        foreach (var info in VirtualCameraInfo.DefaultCameras)
        {
            Cameras.Add(new VirtualCameraState
            {
                SerialNumber = info.SerialNumber,
                DisplayName = info.DisplayName,
                Width = info.Width,
                Height = info.Height,
                Fps = info.Fps,
                Enabled = true,
            });
        }
    }

    public async Task StartServerAsync()
    {
        if (_server != null) return;

        _server = new CameraSimulatorServer(Cameras);
        _server.LogMessage += AppendLog;
        _server.CameraUpdated += OnCameraUpdated;

        try
        {
            await _server.StartAsync(Port);
            IsServerRunning = true;
            ServerStatus = $"Running on port {Port}";
            AppendLog($"Server started on port {Port}");
        }
        catch (Exception ex)
        {
            AppendLog($"Failed to start server: {ex.Message}");
            ServerStatus = "Error";
            _server = null;
        }
    }

    public async Task StopServerAsync()
    {
        if (_server == null) return;
        await _server.StopAsync();
        _server = null;
        IsServerRunning = false;
        ServerStatus = "Stopped";
        AppendLog("Server stopped");
    }

    private void OnCameraUpdated(VirtualCameraState cam)
    {
        Application.Current?.Dispatcher.Invoke(() =>
        {
            var existing = Cameras.FirstOrDefault(c => c.SerialNumber == cam.SerialNumber);
            if (existing != null)
            {
                existing.IsStreaming = cam.IsStreaming;
                existing.FramesSent = cam.FramesSent;
                existing.ActualFps = cam.ActualFps;
            }
        });
    }

    private void AppendLog(string message)
    {
        Application.Current?.Dispatcher.Invoke(() =>
        {
            Logs.Add($"[{DateTime.Now:HH:mm:ss.fff}] {message}");
            if (Logs.Count > 200)
                Logs.RemoveAt(0);
        });
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}