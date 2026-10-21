using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using KW.CameraSimulator.Protocol;
using KW.CameraSimulatorTool.Models;
using KW.CameraSimulatorTool.Services;

namespace KW.CameraSimulatorTool.ViewModels;

public class SimulatorViewModel : INotifyPropertyChanged
{
    private const string PylonCamEmuVariable = "PYLON_CAMEMU";
    private const int DefaultEmulatedCount = 4;

    private CameraSimulatorServer? _server;
    private string _serverStatus = "Stopped";
    private bool _isServerRunning;
    private int _port = CameraProtocol.DefaultPort;
    private string _log = string.Empty;
    private int _emulatedCount;
    private bool _isEmulationEnabled;
    private string _emulationStatus = "Not configured";
    private string _kwinsprintPath = string.Empty;

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

    public int EmulatedCount
    {
        get => _emulatedCount;
        set { _emulatedCount = Math.Clamp(value, 1, 256); OnPropertyChanged(); OnPropertyChanged(nameof(EmulationEnabledDisplay)); }
    }

    public bool IsEmulationEnabled
    {
        get => _isEmulationEnabled;
        set { _isEmulationEnabled = value; OnPropertyChanged(); OnPropertyChanged(nameof(EmulationEnabledDisplay)); }
    }

    public string EmulationStatus
    {
        get => _emulationStatus;
        set { _emulationStatus = value; OnPropertyChanged(); }
    }

    public string EmulationEnabledDisplay => IsEmulationEnabled
        ? $"PYLON_CAMEMU={EmulatedCount}"
        : "PYLON_CAMEMU is not set";

    public string KWinSprintPath
    {
        get => _kwinsprintPath;
        set { _kwinsprintPath = value; OnPropertyChanged(); }
    }

    public ICommand EnableEmulationCommand { get; }
    public ICommand DisableEmulationCommand { get; }
    public ICommand LaunchKWinSprintCommand { get; }

    public SimulatorViewModel()
    {
        EnableEmulationCommand = new RelayCommand(_ => EnableCameraEmulation());
        DisableEmulationCommand = new RelayCommand(_ => DisableCameraEmulation());
        LaunchKWinSprintCommand = new RelayCommand(_ => LaunchKWinSprint(), _ => !string.IsNullOrWhiteSpace(KWinSprintPath));

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

        RefreshEmulationStatus();
    }

    private void RefreshEmulationStatus()
    {
        string? userValue = Environment.GetEnvironmentVariable(PylonCamEmuVariable, EnvironmentVariableTarget.User);
        string? procValue = Environment.GetEnvironmentVariable(PylonCamEmuVariable, EnvironmentVariableTarget.Process);

        string effective = procValue ?? userValue ?? string.Empty;

        if (int.TryParse(effective, out int count) && count > 0)
        {
            IsEmulationEnabled = true;
            EmulatedCount = count;
            EmulationStatus = $"Enabled (PYLON_CAMEMU={count})";

            if (userValue != procValue && !string.IsNullOrEmpty(procValue))
                EmulationStatus += " [process]";
            else if (!string.IsNullOrEmpty(userValue))
                EmulationStatus += " [user]";
        }
        else
        {
            IsEmulationEnabled = false;
            EmulatedCount = DefaultEmulatedCount;
            EmulationStatus = "Not configured — KWinSprint will not discover simulated cameras";
        }
    }

    public void EnableCameraEmulation()
    {
        int count = EmulatedCount > 0 ? EmulatedCount : DefaultEmulatedCount;
        Environment.SetEnvironmentVariable(PylonCamEmuVariable, count.ToString(), EnvironmentVariableTarget.User);
        Environment.SetEnvironmentVariable(PylonCamEmuVariable, count.ToString(), EnvironmentVariableTarget.Process);
        RefreshEmulationStatus();
        AppendLog($"Set {PylonCamEmuVariable}={count} (user + process scope)");
        AppendLog("NOTE: Restart KWinSprint for changes to take effect.");
    }

    public void DisableCameraEmulation()
    {
        Environment.SetEnvironmentVariable(PylonCamEmuVariable, null, EnvironmentVariableTarget.User);
        Environment.SetEnvironmentVariable(PylonCamEmuVariable, null, EnvironmentVariableTarget.Process);
        RefreshEmulationStatus();
        AppendLog($"Cleared {PylonCamEmuVariable} (user + process scope)");
    }

    public void LaunchKWinSprint()
    {
        if (string.IsNullOrWhiteSpace(KWinSprintPath)) return;

        if (!IsEmulationEnabled)
        {
            EnableCameraEmulation();
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = KWinSprintPath,
                UseShellExecute = false,
                WorkingDirectory = System.IO.Path.GetDirectoryName(KWinSprintPath) ?? string.Empty,
            };

            psi.Environment[PylonCamEmuVariable] = EmulatedCount.ToString();

            Process.Start(psi);
            AppendLog($"Launched KWinSprint with {PylonCamEmuVariable}={EmulatedCount}");
        }
        catch (Exception ex)
        {
            AppendLog($"Failed to launch KWinSprint: {ex.Message}");
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

public class RelayCommand : ICommand
{
    private readonly Action<object?> _execute;
    private readonly Predicate<object?>? _canExecute;

    public RelayCommand(Action<object?> execute, Predicate<object?>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;
    public void Execute(object? parameter) => _execute(parameter);
    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }
}