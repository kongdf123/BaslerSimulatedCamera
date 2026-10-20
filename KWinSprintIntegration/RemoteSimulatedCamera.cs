using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using KW.Capture.Camera.Support;
using KW.Capture.Camera.Support.Basler;
using OpenCvSharp;

namespace KW.Capture.Camera;

/// <summary>
/// 远程模拟相机：连接 CameraSimulatorTool 桌面应用，通过 TCP 接收 BGR24 帧，
/// 用于无真机时的联调 / 测试会话。
/// </summary>
public sealed class RemoteSimulatedCamera : CameraBase
{
    private const string SimulatorHost = "127.0.0.1";
    private const int SimulatorPort = 5270;
    private const int ConnectTimeoutMs = 2000;
    private const int DefaultFrameWidth = 640;
    private const int DefaultFrameHeight = 480;
    private const double DefaultFrameRate = 100.0;

    private readonly object _syncRoot = new();
    private string _errorMessage = string.Empty;
    private bool _isOpen;
    private bool _reverseX;
    private int _slotIndex;
    private int _frameWidth = DefaultFrameWidth;
    private int _frameHeight = DefaultFrameHeight;
    private long _recordedFrameCount;
    private bool _isRecordingPaused;

    private TcpClient? _tcpClient;
    private NetworkStream? _stream;
    private Thread? _receiveThread;
    private CancellationTokenSource? _receiveCts;

    private VideoWriter? _videoWriter;
    private CameraParameterSettings _parameterSettings = CameraParameterSettings.CreateDefaults();

    /// <inheritdoc />
    public override CameraDriver Driver => CameraDriver.RemoteSimulated;

    /// <inheritdoc />
    public override long RecordedFrameCount => Interlocked.Read(ref _recordedFrameCount);

    /// <inheritdoc />
    public override int LiveFrameWidth => _frameWidth;

    /// <inheritdoc />
    public override int LiveFrameHeight => _frameHeight;

    /// <inheritdoc />
    public override bool IsRecordingPaused => _isRecordingPaused;

    /// <inheritdoc />
    public override void PauseRecording()
    {
        if (!IsRecording) return;
        _isRecordingPaused = true;
    }

    /// <inheritdoc />
    public override void ResumeRecording()
    {
        if (!IsRecording) return;
        _isRecordingPaused = false;
    }

    /// <inheritdoc />
    public override bool IsCameraDriverAvailable() => true;

    /// <inheritdoc />
    public override IReadOnlyList<CameraDeviceInfo> GetCameraDeviceList()
    {
        var devices = new List<CameraDeviceInfo>();
        try
        {
            using var client = new TcpClient();
            if (!client.ConnectAsync(SimulatorHost, SimulatorPort).Wait(ConnectTimeoutMs))
                return devices;

            using var stream = client.GetStream();
            SendCommand(stream, "LIST");
            string response = ReadLine(stream, 3000);
            if (string.IsNullOrWhiteSpace(response) || response.StartsWith("ERROR", StringComparison.OrdinalIgnoreCase))
                return devices;

            var infos = JsonSerializer.Deserialize<RemoteCameraInfo[]>(response);
            if (infos == null) return devices;

            foreach (var info in infos)
            {
                devices.Add(new CameraDeviceInfo
                {
                    SerialNumber = info.SerialNumber,
                    Vendor = "RemoteSimulated",
                    Driver = CameraDriver.RemoteSimulated,
                    Transport = CameraTransport.GigE,
                    ModelName = "RemoteSimulatedCamera",
                    DisplayName = $"REM: {info.DisplayName}",
                });
            }
        }
        catch
        {
        }
        return devices;
    }

    /// <inheritdoc />
    public override bool InitCamera(string serialNumber)
    {
        if (string.IsNullOrWhiteSpace(serialNumber))
        {
            _errorMessage = "远程模拟相机序列号不能为空。";
            return false;
        }

        if (!serialNumber.StartsWith("SIM-CAM-", StringComparison.OrdinalIgnoreCase))
        {
            _errorMessage = $"不是远程模拟相机序列号：{serialNumber}";
            return false;
        }

        string numberPart = serialNumber.Substring("SIM-CAM-".Length);
        if (!int.TryParse(numberPart, out int slot) || slot < 1 || slot > 4)
        {
            _errorMessage = $"无法解析远程模拟相机序号：{serialNumber}";
            return false;
        }

        SerialNumber = serialNumber.ToUpperInvariant();
        _slotIndex = slot;
        Transport = CameraTransport.GigE;
        _errorMessage = string.Empty;
        return true;
    }

    /// <inheritdoc />
    public override bool OpenCamera()
    {
        if (string.IsNullOrWhiteSpace(SerialNumber))
        {
            _errorMessage = "请先 InitCamera。";
            return false;
        }

        try
        {
            _tcpClient = new TcpClient();
            if (!_tcpClient.ConnectAsync(SimulatorHost, SimulatorPort).Wait(ConnectTimeoutMs))
            {
                _errorMessage = $"无法连接到模拟器 {SimulatorHost}:{SimulatorPort}（超时）。";
                _tcpClient.Dispose();
                _tcpClient = null;
                return false;
            }

            _stream = _tcpClient.GetStream();
            SendCommand(_stream, $"OPEN {SerialNumber}");
            string response = ReadLine(_stream, 3000);
            if (!string.Equals(response, "OK", StringComparison.OrdinalIgnoreCase))
            {
                _errorMessage = $"打开远程相机失败：{response}";
                _stream.Dispose();
                _tcpClient.Dispose();
                _stream = null;
                _tcpClient = null;
                return false;
            }

            _isOpen = true;
            _errorMessage = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            _errorMessage = $"打开远程相机异常：{ex.Message}";
            return false;
        }
    }

    /// <inheritdoc />
    public override void CloseCamera()
    {
        if (IsRecording) StopRecord();
        if (IsCollecting) StopCollect();

        try
        {
            if (_stream != null)
            {
                try { SendCommand(_stream, "CLOSE"); } catch { }
            }
        }
        catch { }

        _stream?.Dispose();
        _stream = null;
        _tcpClient?.Dispose();
        _tcpClient = null;
        _isOpen = false;
    }

    /// <inheritdoc />
    public override bool StartCollect()
    {
        if (!CanStartCollect())
        {
            _errorMessage = _isOpen ? "已经在采集中。" : "相机未打开，无法开始采集。";
            return false;
        }

        try
        {
            SendCommand(_stream!, "START");
            string response = ReadLine(_stream!, 3000);
            if (!string.Equals(response, "OK", StringComparison.OrdinalIgnoreCase))
            {
                _errorMessage = $"启动远程采集失败：{response}";
                return false;
            }

            _receiveCts = new CancellationTokenSource();
            _receiveThread = new Thread(ReceiveLoop) { IsBackground = true, Name = $"RemoteCam-{SerialNumber}" };
            _receiveThread.Start(_receiveCts.Token);

            IsCollecting = true;
            FPS = (float)DefaultFrameRate;
            _errorMessage = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            _errorMessage = $"启动远程采集异常：{ex.Message}";
            return false;
        }
    }

    /// <inheritdoc />
    public override bool StopCollect()
    {
        if (!IsCollecting) return true;

        try { SendCommand(_stream!, "STOP"); } catch { }

        _receiveCts?.Cancel();
        _receiveThread?.Join(2000);
        _receiveCts?.Dispose();
        _receiveCts = null;
        _receiveThread = null;

        IsCollecting = false;
        _errorMessage = string.Empty;
        return true;
    }

    private void ReceiveLoop(object? state)
    {
        var ct = (CancellationToken)state!;
        byte[] headerBuffer = new byte[16];

        try
        {
            while (!ct.IsCancellationRequested && _stream != null)
            {
                if (!ReadExact(_stream, headerBuffer, 16)) break;

                int width = BitConverter.ToInt32(headerBuffer, 0);
                int height = BitConverter.ToInt32(headerBuffer, 4);
                int pixelFormat = BitConverter.ToInt32(headerBuffer, 8);
                int byteCount = BitConverter.ToInt32(headerBuffer, 12);

                if (width <= 0 || height <= 0 || byteCount <= 0) break;

                byte[] pixels = new byte[byteCount];
                if (!ReadExact(_stream, pixels, byteCount)) break;

                _frameWidth = width;
                _frameHeight = height;

                var frameArgs = new CameraFrameEventArgs(
                    width, height, CameraPixelFormat.Bgr24, pixels, DateTime.UtcNow);

                RaiseFrameReceived(frameArgs);

                if (IsRecording && !_isRecordingPaused)
                {
                    WriteRecordedFrame(pixels, width, height);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _errorMessage = $"远程帧接收异常：{ex.Message}";
        }
    }

    private static bool ReadExact(NetworkStream stream, byte[] buffer, int count)
    {
        int offset = 0;
        while (offset < count)
        {
            int read = stream.Read(buffer, offset, count - offset);
            if (read <= 0) return false;
            offset += read;
        }
        return true;
    }

    /// <inheritdoc />
    public override bool StartRecord(string filePath)
    {
        if (!CanStartRecord())
        {
            _errorMessage = _isOpen ? "已经在录像中。" : "相机未打开，无法开始录像。";
            return false;
        }

        if (string.IsNullOrWhiteSpace(filePath))
        {
            _errorMessage = "录像路径不能为空。";
            return false;
        }

        try
        {
            string? directory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            var writer = new VideoWriter();
            bool opened = writer.Open(
                filePath,
                VideoRecordingCodec.ResolveFourCC(filePath),
                DefaultFrameRate,
                new Size(_frameWidth, _frameHeight));

            if (!opened || !writer.IsOpened())
            {
                writer.Dispose();
                _errorMessage = $"无法创建录像文件：{filePath}";
                return false;
            }

            lock (_syncRoot)
            {
                _videoWriter = writer;
                Interlocked.Exchange(ref _recordedFrameCount, 0);
                ResetPreviewStatisticsForRecordingStart();
                _isRecordingPaused = false;
                IsRecording = true;
            }

            _errorMessage = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            _errorMessage = $"创建录像文件失败：{ex.Message}";
            return false;
        }
    }

    /// <inheritdoc />
    public override void StopRecord()
    {
        if (!IsRecording) return;

        lock (_syncRoot)
        {
            VideoWriter? writer = _videoWriter;
            _videoWriter = null;
            IsRecording = false;
            _isRecordingPaused = false;

            if (writer == null) return;

            try
            {
                writer.Release();
                writer.Dispose();
            }
            catch (Exception ex)
            {
                _errorMessage = $"结束录像失败：{ex.Message}";
            }
        }
    }

    private void WriteRecordedFrame(byte[] pixels, int width, int height)
    {
        lock (_syncRoot)
        {
            VideoWriter? writer = _videoWriter;
            if (writer == null) return;

            try
            {
                using Mat mat = Mat.FromPixelData(height, width, MatType.CV_8UC3, pixels);
                writer.Write(mat);
                Interlocked.Increment(ref _recordedFrameCount);
            }
            catch (Exception ex)
            {
                _errorMessage = $"写入录像帧失败：{ex.Message}";
            }
        }
    }

    /// <inheritdoc />
    public override void Photograph(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            _errorMessage = "抓拍路径不能为空。";
            return;
        }

        try
        {
            string? directory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            var pixels = GenerateSnapshotPixels();
            using Mat mat = Mat.FromPixelData(_frameHeight, _frameWidth, MatType.CV_8UC3, pixels);
            Cv2.ImWrite(filePath, mat);
            _errorMessage = string.Empty;
        }
        catch (Exception ex)
        {
            _errorMessage = $"抓拍失败：{ex.Message}";
        }
    }

    private byte[] GenerateSnapshotPixels()
    {
        int byteCount = _frameWidth * _frameHeight * 3;
        byte[] buffer = new byte[byteCount];
        byte baseR = (byte)(40 + (_slotIndex * 40));
        byte baseG = (byte)(80 + (_slotIndex * 20));
        byte baseB = (byte)(120 + (_slotIndex * 10));

        for (int y = 0; y < _frameHeight; y++)
        {
            for (int x = 0; x < _frameWidth; x++)
            {
                int offset = ((y * _frameWidth) + x) * 3;
                byte shade = (byte)(x % 256);
                buffer[offset + 0] = (byte)((baseB + shade) % 256);
                buffer[offset + 1] = (byte)((baseG + (shade / 2)) % 256);
                buffer[offset + 2] = (byte)((baseR + (y % 64)) % 256);
            }
        }
        return buffer;
    }

    /// <inheritdoc />
    public override void SetReverseX(bool reverseX) => _reverseX = reverseX;

    /// <inheritdoc />
    public override bool GetReverseX() => _reverseX;

    /// <inheritdoc />
    public override CameraParameterSettings GetCameraParameterSettings()
    {
        lock (_syncRoot) return _parameterSettings.Clone();
    }

    /// <inheritdoc />
    public override void UpdateCameraParameterSettings(CameraParameterSettings settings)
    {
        if (settings == null) return;
        lock (_syncRoot) _parameterSettings = settings.Clone();
    }

    /// <inheritdoc />
    public override bool TryWriteCameraParameterSettings(out string? errorMessage)
    {
        errorMessage = null;
        lock (_syncRoot)
        {
            double fps = Math.Clamp(_parameterSettings.FrameRate, 1, 200);
            FPS = (float)fps;
        }
        return true;
    }

    /// <inheritdoc />
    public override CameraParameterReadBackResult TryReadCameraParameterSettings()
    {
        CameraParameterSettings settings = GetCameraParameterSettings();
        settings.Width = _frameWidth;
        settings.Height = _frameHeight;
        settings.FrameRate = FPS > 0 ? FPS : (float)DefaultFrameRate;

        return new CameraParameterReadBackResult
        {
            IsSuccess = true,
            Settings = settings,
            Warnings = new[] { "远程模拟相机：参数仅保存在内存。" },
            ParameterLimits = CameraParameterLimits.CreateFallback(),
        };
    }

    /// <inheritdoc />
    protected override string GetErrorMsg() => _errorMessage;

    /// <inheritdoc />
    protected override bool CheckIsConnected() => _isOpen && _tcpClient != null && _tcpClient.Connected;

    private static void SendCommand(NetworkStream stream, string command)
    {
        byte[] data = Encoding.ASCII.GetBytes(command + "\r\n");
        stream.Write(data, 0, data.Length);
        stream.Flush();
    }

    private static string ReadLine(NetworkStream stream, int timeoutMs)
    {
        var sb = new StringBuilder();
        var buffer = new byte[1];
        stream.ReadTimeout = timeoutMs;

        while (true)
        {
            int read = stream.Read(buffer, 0, 1);
            if (read <= 0) break;
            char c = (char)buffer[0];
            if (c == '\n') break;
            if (c != '\r') sb.Append(c);
        }

        return sb.ToString();
    }

    private sealed class RemoteCameraInfo
    {
        public string SerialNumber { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public int Width { get; set; }
        public int Height { get; set; }
        public int Fps { get; set; }
    }

    protected override void DisposeManagedResources()
    {
        CloseCamera();
    }
}