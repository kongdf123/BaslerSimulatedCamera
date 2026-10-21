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
    private bool _allowRemoteConnections;
    private string _log = string.Empty;
    private int _emulatedCount;
    private bool _isEmulationEnabled;
    private string _emulationStatus = "Not configured";
    private string _kwinsprintPath = string.Empty;
    private int _connectedClients;

    public ObservableCollection<VirtualCameraState> Cameras { get; } = new();
    public ObservableCollection<string> Logs { get; } = new();

    public IReadOnlyList<string> TestPatterns => KW.CameraSimulator.Protocol.TestPatterns.All;

    public int Port
    {
        get => _port;
        set { _port = value; OnPropertyChanged(); }
    }

    public bool AllowRemoteConnections
    {
        get => _allowRemoteConnections;
        set { _allowRemoteConnections = value; OnPropertyChanged(); OnPropertyChanged(nameof(BindAddressDisplay)); }
    }

    public string BindAddressDisplay => _allowRemoteConnections ? "Binding: 0.0.0.0 (all interfaces)" : "Binding: 127.0.0.1 (loopback only)";

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

    public int ConnectedClients
    {
        get => _connectedClients;
        set { _connectedClients = value; OnPropertyChanged(); }
    }

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
                TestPattern = KW.CameraSimulator.Protocol.TestPatterns.ColorBars,
            });
        }

        RefreshEmulationStatus();
        AutoSyncUserToProcessScope();
        ProbeAndFixPylonRuntime();
    }

    private void ProbeAndFixPylonRuntime()
    {
        try
        {
            var result = PylonRuntimeFixer.CheckAndFix();
            if (result.NeedsFix && result.FixApplied)
                AppendLog($"PylonRuntime auto-fixed: {result.Message}");
            else if (result.NeedsFix)
                AppendLog($"PylonRuntime incomplete: {result.Message} (Enable Emulation to retry)");
            else
                AppendLog($"PylonRuntime OK — camera emulation DLLs present.");
        }
        catch (Exception ex)
        {
            AppendLog($"PylonRuntime probe error: {ex.Message}");
        }
    }

    private void AutoSyncUserToProcessScope()
    {
        string? procValue = Environment.GetEnvironmentVariable(PylonCamEmuVariable, EnvironmentVariableTarget.Process);
        string? userValue = Environment.GetEnvironmentVariable(PylonCamEmuVariable, EnvironmentVariableTarget.User);

        if (string.IsNullOrEmpty(procValue) && !string.IsNullOrEmpty(userValue))
        {
            Environment.SetEnvironmentVariable(PylonCamEmuVariable, userValue, EnvironmentVariableTarget.Process);
            RefreshEmulationStatus();
        }
    }

    private void RefreshEmulationStatus()
    {
        string? procValue = Environment.GetEnvironmentVariable(PylonCamEmuVariable, EnvironmentVariableTarget.Process);
        string? userValue = Environment.GetEnvironmentVariable(PylonCamEmuVariable, EnvironmentVariableTarget.User);

        string effective = procValue ?? userValue ?? string.Empty;

        if (int.TryParse(effective, out int count) && count > 0)
        {
            IsEmulationEnabled = true;
            EmulatedCount = count;

            bool hasProc = !string.IsNullOrEmpty(procValue);
            bool hasUser = !string.IsNullOrEmpty(userValue);
            EmulationStatus = $"PYLON_CAMEMU={count}";
            if (hasProc && hasUser)
                EmulationStatus += " (user + process scope)";
            else if (hasProc)
                EmulationStatus += " (process scope only)";
            else if (hasUser)
                EmulationStatus += " (user scope only — launch KWinSprint via button to inject into process scope)";
        }
        else
        {
            IsEmulationEnabled = false;
            EmulatedCount = DefaultEmulatedCount;
            EmulationStatus = "Not configured — click Enable below to make KWinSprint discover simulated cameras";
        }
    }

    public void EnableCameraEmulation()
    {
        AppendLog("Checking PylonRuntime for camera emulation support...");
        var fixResult = PylonRuntimeFixer.CheckAndFix(msg => AppendLog($"  {msg}"));

        if (fixResult.NeedsFix && !fixResult.FixApplied)
        {
            AppendLog("WARNING: PylonRuntime could not be fixed. Camera emulation may not work.");
            AppendLog("  Try installing full Basler pylon or manually copy PylonCamEmu_v12_TL.dll.");
        }
        else if (fixResult.FixApplied)
        {
            AppendLog("PylonRuntime repaired successfully — camera emulation DLLs installed.");
        }

        int count = EmulatedCount > 0 ? EmulatedCount : DefaultEmulatedCount;
        Environment.SetEnvironmentVariable(PylonCamEmuVariable, count.ToString(), EnvironmentVariableTarget.User);
        Environment.SetEnvironmentVariable(PylonCamEmuVariable, count.ToString(), EnvironmentVariableTarget.Process);
        RefreshEmulationStatus();
        AppendLog($"Set {PylonCamEmuVariable}={count} (user + process scope)");
        AppendLog("KWinSprint will discover the emulated Basler cameras on next launch.");
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

        foreach (var cam in Cameras) cam.Validate();
        bool hasErrors = Cameras.Any(c => c.HasValidationError);
        if (hasErrors)
        {
            AppendLog("Cannot start server: one or more cameras have invalid configuration.");
            ServerStatus = "Configuration error";
            return;
        }

        _server = new CameraSimulatorServer(Cameras);
        _server.LogMessage += AppendLog;
        _server.CameraUpdated += OnCameraUpdated;
        _server.ConnectionCountChanged += OnConnectionCountChanged;

        try
        {
            _server.Start(Port, _allowRemoteConnections);
            IsServerRunning = true;
            ServerStatus = $"Running on port {Port} ({(_allowRemoteConnections ? "all interfaces" : "loopback")})";
            AppendLog($"TCP server ready — connect clients to 127.0.0.1:{Port}");
            AppendLog("NOTE: This TCP server is for RemoteSimulatedCamera clients only.");
            AppendLog("To make KWinSprint discover simulated cameras, use the Enable Emulation + Launch KWinSprint above.");
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
        _server.ConnectionCountChanged -= OnConnectionCountChanged;
        _server = null;
        IsServerRunning = false;
        ConnectedClients = 0;
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

    private void OnConnectionCountChanged(int count)
    {
        Application.Current?.Dispatcher.Invoke(() => ConnectedClients = count);
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