using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Diagnostics;
using KW.Capture.Camera.Support;
using KW.Capture.Camera.Support.Basler;
using OpenCvSharp;

namespace KW.Capture.Camera;

public sealed class RemoteSimulatedCamera : CameraBase
{
    private const string SimulatorHost = "127.0.0.1";
    private const int SimulatorPort = 5270;
    private const int SimulatorFramePort = 5271;
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
    private TcpClient? _frameTcpClient;
    private NetworkStream? _frameStream;
    private Thread? _receiveThread;
    private CancellationTokenSource? _receiveCts;

    private VideoWriter? _videoWriter;
    private CameraParameterSettings _parameterSettings = CameraParameterSettings.CreateDefaults();
    private readonly FrameStatistics _stats = new();

    public override CameraDriver Driver => CameraDriver.RemoteSimulated;

    public override long RecordedFrameCount => Interlocked.Read(ref _recordedFrameCount);

    public override int LiveFrameWidth => _frameWidth;

    public override int LiveFrameHeight => _frameHeight;

    public override bool IsRecordingPaused => _isRecordingPaused;

    public override double MeasuredFps => _stats.GetInstantFps();

    public long FramesReceived => _stats.FramesReceived;

    public long FramesDropped => _stats.FramesDropped;

    public override void PauseRecording()
    {
        if (!IsRecording) return;
        _isRecordingPaused = true;
    }

    public override void ResumeRecording()
    {
        if (!IsRecording) return;
        _isRecordingPaused = false;
    }

    public override bool IsCameraDriverAvailable() => true;

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

            try
            {
                SendCommand(_stream, $"GET_CONFIG {SerialNumber}");
                string cfgResp = ReadLine(_stream, 3000);
                if (!cfgResp.StartsWith("ERROR", StringComparison.OrdinalIgnoreCase))
                {
                    var cfg = JsonSerializer.Deserialize<RemoteConfig>(cfgResp);
                    if (cfg != null)
                    {
                        _frameWidth = cfg.Width;
                        _frameHeight = cfg.Height;
                        FPS = (float)cfg.Fps;
                    }
                }
            }
            catch { }

            _stats.Reset();
            _errorMessage = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            _errorMessage = $"打开远程相机异常：{ex.Message}";
            return false;
        }
    }

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

        _frameStream?.Dispose();
        _frameStream = null;
        _frameTcpClient?.Dispose();
        _frameTcpClient = null;

        _stream?.Dispose();
        _stream = null;
        _tcpClient?.Dispose();
        _tcpClient = null;
        _isOpen = false;
    }

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
            string startResp = ReadLine(_stream!, 3000);

            string? streamId = null;
            if (!string.IsNullOrWhiteSpace(startResp) &&
                startResp.StartsWith("OK ", StringComparison.OrdinalIgnoreCase))
            {
                int eqIdx = startResp.IndexOf("sid=", StringComparison.OrdinalIgnoreCase);
                if (eqIdx >= 0) streamId = startResp.Substring(eqIdx + 4).Trim();
            }

            if (string.IsNullOrWhiteSpace(streamId))
            {
                _errorMessage = $"启动远程采集失败：{startResp}";
                return false;
            }

            try
            {
                _frameTcpClient = new TcpClient();
                if (!_frameTcpClient.ConnectAsync(SimulatorHost, SimulatorFramePort).Wait(ConnectTimeoutMs))
                {
                    _errorMessage = $"无法连接到模拟器帧端口 {SimulatorHost}:{SimulatorFramePort}（超时）。";
                    _frameTcpClient.Dispose();
                    _frameTcpClient = null;
                    return false;
                }

                _frameStream = _frameTcpClient.GetStream();
                SendCommand(_frameStream, $"FRAME_AUTH {streamId}");
                string authResp = ReadLine(_frameStream, 3000);
                if (!string.Equals(authResp, "OK", StringComparison.OrdinalIgnoreCase))
                {
                    _errorMessage = $"帧通道认证失败：{authResp}";
                    _frameStream.Dispose();
                    _frameStream = null;
                    _frameTcpClient.Dispose();
                    _frameTcpClient = null;
                    return false;
                }
            }
            catch (Exception ex)
            {
                _errorMessage = $"帧通道连接异常：{ex.Message}";
                _frameStream?.Dispose();
                _frameStream = null;
                _frameTcpClient?.Dispose();
                _frameTcpClient = null;
                return false;
            }

            _receiveCts = new CancellationTokenSource();
            _receiveThread = new Thread(ReceiveLoop) { IsBackground = true, Name = $"RemoteCam-{SerialNumber}" };
            _receiveThread.Start(_receiveCts.Token);

            IsCollecting = true;
            _stats.Reset();
            _errorMessage = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            _errorMessage = $"启动远程采集异常：{ex.Message}";
            return false;
        }
    }

    public override bool StopCollect()
    {
        if (!IsCollecting) return true;

        try { SendCommand(_stream!, "STOP"); } catch { }

        try { _frameStream?.Close(); } catch { }

        _receiveCts?.Cancel();
        _receiveThread?.Join(2000);
        _receiveCts?.Dispose();
        _receiveCts = null;
        _receiveThread = null;

        _frameStream?.Dispose();
        _frameStream = null;
        _frameTcpClient?.Dispose();
        _frameTcpClient = null;

        IsCollecting = false;
        _errorMessage = string.Empty;
        return true;
    }

    private void ReceiveLoop(object? state)
    {
        var ct = (CancellationToken)state!;
        byte[] headerBuffer = new byte[KW.CameraSimulator.Protocol.CameraProtocol.FrameHeaderSize];
        var frameStream = _frameStream;

        try
        {
            while (!ct.IsCancellationRequested && frameStream != null && _frameStream != null)
            {
                if (!ReadExact(_frameStream, headerBuffer, headerBuffer.Length)) break;

                if (!KW.CameraSimulator.Protocol.CameraProtocol.TryParseFrameHeader(
                    headerBuffer, out int width, out int height, out int pixelFormat, out int byteCount))
                {
                    _stats.OnFrameDropped();
                    break;
                }

                if (pixelFormat != KW.CameraSimulator.Protocol.CameraProtocol.PixelFormatBgr24)
                {
                    _stats.OnFrameDropped();
                    break;
                }

                try
                {
                    KW.CameraSimulator.Protocol.FrameTransport.ValidateBgr24PayloadLength(
                        width, height, byteCount);
                }
                catch
                {
                    _stats.OnFrameDropped();
                    break;
                }

                byte[] pixels = new byte[byteCount];
                if (!ReadExact(_frameStream, pixels, byteCount)) break;

                _frameWidth = width;
                _frameHeight = height;

                _stats.OnFrameReceived();

                var frameArgs = new CameraFrameEventArgs(
                    width, height, CameraPixelFormat.Bgr24, pixels, DateTime.UtcNow);

                RaiseFrameReceived(frameArgs);

                if (IsRecording && !_isRecordingPaused)
                {
                    WriteRecordedFrame(pixels, width, height);
                }
            }
        }
        catch (IOException ex)
        {
            if (!ct.IsCancellationRequested)
                _errorMessage = $"Simulator connection lost: {ex.Message}";
        }
        catch (ObjectDisposedException) { }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!ct.IsCancellationRequested)
                _errorMessage = $"远程帧接收异常：{ex.Message}";
        }
        finally
        {
            IsCollecting = false;

            if (!ct.IsCancellationRequested)
                _isOpen = false;

            if (!ct.IsCancellationRequested)
            {
                try { _frameStream?.Dispose(); } catch { }
                try { _frameTcpClient?.Dispose(); } catch { }
                _frameStream = null;
                _frameTcpClient = null;
                try { _stream?.Dispose(); } catch { }
                try { _tcpClient?.Dispose(); } catch { }
                _stream = null;
                _tcpClient = null;
            }
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

    public override void SetReverseX(bool reverseX) => _reverseX = reverseX;

    public override bool GetReverseX() => _reverseX;

    public override CameraParameterSettings GetCameraParameterSettings()
    {
        lock (_syncRoot) return _parameterSettings.Clone();
    }

    public override void UpdateCameraParameterSettings(CameraParameterSettings settings)
    {
        if (settings == null) return;
        lock (_syncRoot) _parameterSettings = settings.Clone();
    }

    public override bool TryWriteCameraParameterSettings(out string? errorMessage)
    {
        errorMessage = null;

        try
        {
            CameraParameterSettings settings;
            lock (_syncRoot) settings = _parameterSettings.Clone();

            bool sentToServer = false;

            if (_isOpen && _stream != null && !string.IsNullOrWhiteSpace(SerialNumber))
            {
                var payload = new
                {
                    width = Math.Clamp(settings.Width, 64, 1920),
                    height = Math.Clamp(settings.Height, 64, 1080),
                    fps = Math.Clamp(settings.FrameRate, 1, 240),
                };

                string json = JsonSerializer.Serialize(payload);
                SendCommand(_stream, $"SET_CONFIG {SerialNumber} {json}");
                string response = ReadLine(_stream, 3000);

                if (response.StartsWith("ERROR", StringComparison.OrdinalIgnoreCase))
                {
                    errorMessage = response;
                    return false;
                }

                try
                {
                    var applied = JsonSerializer.Deserialize<RemoteConfig>(response);
                    if (applied != null)
                    {
                        _frameWidth = applied.Width;
                        _frameHeight = applied.Height;
                        FPS = (float)applied.Fps;
                    }
                }
                catch { }

                sentToServer = true;
            }

            double fps = Math.Clamp(settings.FrameRate, 1, 200);
            FPS = (float)fps;

            if (!sentToServer)
            {
                errorMessage = "相机未打开，参数仅保存在本地，未同步到模拟器。";
            }

            return sentToServer;
        }
        catch (Exception ex)
        {
            errorMessage = $"写入远程相机参数异常：{ex.Message}";
            return false;
        }
    }

    public override CameraParameterReadBackResult TryReadCameraParameterSettings()
    {
        CameraParameterSettings settings = GetCameraParameterSettings();
        settings.Width = _frameWidth;
        settings.Height = _frameHeight;
        settings.FrameRate = FPS > 0 ? FPS : (float)DefaultFrameRate;

        string[] warnings = _isOpen && _stream != null && !string.IsNullOrWhiteSpace(SerialNumber)
            ? Array.Empty<string>()
            : new[] { "相机未打开：参数为本地缓存值。" };

        return new CameraParameterReadBackResult
        {
            IsSuccess = true,
            Settings = settings,
            Warnings = warnings,
            ParameterLimits = CameraParameterLimits.CreateFallback(),
        };
    }

    protected override string GetErrorMsg() => _errorMessage;

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

    private sealed class RemoteConfig
    {
        public int Width { get; set; }
        public int Height { get; set; }
        public double Fps { get; set; }
        public int PixelFormat { get; set; }
        public string TestPattern { get; set; } = string.Empty;
    }

    private sealed class FrameStatistics
    {
        private long _received;
        private long _dropped;
        private long _lastFrameTimestamp;
        private long _prevCount;
        private long _prevTicks;

        private readonly Stopwatch _clock = Stopwatch.StartNew();

        public void OnFrameReceived()
        {
            Interlocked.Increment(ref _received);
            Interlocked.Exchange(ref _lastFrameTimestamp, _clock.ElapsedTicks);
        }

        public void OnFrameDropped() =>
            Interlocked.Increment(ref _dropped);

        public long FramesReceived => Interlocked.Read(ref _received);
        public long FramesDropped => Interlocked.Read(ref _dropped);

        public double GetInstantFps()
        {
            long nowTicks = _clock.ElapsedTicks;
            long nowCount = FramesReceived;
            long elapsedTicks = nowTicks - _prevTicks;

            if (elapsedTicks <= 0)
            {
                _prevCount = nowCount;
                _prevTicks = nowTicks;
                return 0;
            }

            double fps = (nowCount - _prevCount)
                * (double)Stopwatch.Frequency / elapsedTicks;

            _prevCount = nowCount;
            _prevTicks = nowTicks;

            return fps;
        }

        public long LastFrameAgeMilliseconds
        {
            get
            {
                long last = Interlocked.Read(ref _lastFrameTimestamp);
                if (last == 0)
                    return -1;

                return (long)((_clock.ElapsedTicks - last)
                    * 1000.0 / Stopwatch.Frequency);
            }
        }

        public void Reset()
        {
            Interlocked.Exchange(ref _received, 0);
            Interlocked.Exchange(ref _dropped, 0);
            Interlocked.Exchange(ref _lastFrameTimestamp, 0);
            _prevCount = 0;
            _prevTicks = 0;
            _clock.Restart();
        }
    }

    protected override void DisposeManagedResources()
    {
        CloseCamera();
    }
}