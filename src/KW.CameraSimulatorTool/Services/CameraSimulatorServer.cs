using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using KW.CameraSimulator.Protocol;
using KW.CameraSimulatorTool.Models;

namespace KW.CameraSimulatorTool.Services;

public class CameraSimulatorServer : IDisposable
{
    private readonly ConcurrentDictionary<string, VirtualCameraState> _cameras;
    private readonly ConcurrentDictionary<string, ClientSession> _sessions = new();
    private TcpListener? _listener;
    private CancellationTokenSource? _serverCts;
    private int _port;
    private bool _disposed;
    private bool _allowRemoteConnections;

    public event Action<string>? LogMessage;
    public event Action<VirtualCameraState>? CameraUpdated;
    public event Action<int>? ConnectionCountChanged;

    public IReadOnlyCollection<VirtualCameraState> Cameras => _cameras.Values.ToList();

    public CameraSimulatorServer(IEnumerable<VirtualCameraState> cameras)
    {
        _cameras = new ConcurrentDictionary<string, VirtualCameraState>(
            cameras.ToDictionary(c => c.SerialNumber, c => c));
    }

    public bool IsRunning => _listener != null;
    public int Port => _port;
    public bool AllowRemoteConnections => _allowRemoteConnections;
    public int ConnectedClients => _sessions.Count;

    public void Start(int port, bool allowRemoteConnections = false)
    {
        if (_listener != null)
            throw new InvalidOperationException("Server is already running.");

        _port = port;
        _allowRemoteConnections = allowRemoteConnections;
        _serverCts = new CancellationTokenSource();

        IPAddress address = allowRemoteConnections ? IPAddress.Any : IPAddress.Loopback;
        _listener = new TcpListener(address, port);
        _listener.Start();

        string bindInfo = allowRemoteConnections ? "0.0.0.0" : "127.0.0.1";
        Log($"TCP server listening on {bindInfo}:{port}");

        _ = AcceptLoopAsync(_serverCts.Token);
    }

    public Task StartAsync(int port, bool allowRemoteConnections = false)
    {
        Start(port, allowRemoteConnections);
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        if (_listener == null)
            return;

        _serverCts?.Cancel();
        _listener.Stop();
        _listener = null;

        foreach (var session in _sessions.Values)
        {
            try { session.Dispose(); } catch { }
        }
        _sessions.Clear();

        foreach (var cam in _cameras.Values)
        {
            cam.IsStreaming = false;
            cam.FramesSent = 0;
            cam.ActualFps = 0;
            CameraUpdated?.Invoke(cam);
        }

        Log("Server stopped.");
        await Task.CompletedTask;
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener != null)
        {
            try
            {
                var client = await _listener.AcceptTcpClientAsync(ct);
                var session = new ClientSession(client, this);
                _sessions[session.Id] = session;
                ConnectionCountChanged?.Invoke(_sessions.Count);
                Log($"Client connected: {session.RemoteEndPoint} ({session.Id})");
                _ = session.RunAsync(ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Log($"Accept error: {ex.Message}");
            }
        }
    }

    internal VirtualCameraState? GetCamera(string serial)
    {
        _cameras.TryGetValue(serial, out var cam);
        return cam;
    }

    internal bool IsCameraStreamingByAnySession(string serial)
    {
        return _sessions.Values.Any(s => s.IsStreaming && s.OpenCameraSerial == serial);
    }

    internal VirtualCameraState? GetCameraForConfig(string serial)
    {
        _cameras.TryGetValue(serial, out var cam);
        return cam;
    }

    internal void OnCameraStateChanged(VirtualCameraState cam)
    {
        CameraUpdated?.Invoke(cam);
    }

    internal void Log(string message)
    {
        LogMessage?.Invoke(message);
    }

    internal void RemoveSession(string id)
    {
        _sessions.TryRemove(id, out _);
        ConnectionCountChanged?.Invoke(_sessions.Count);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { StopAsync().Wait(TimeSpan.FromSeconds(2)); } catch { }
        _serverCts?.Dispose();
    }

    private class ClientSession : IDisposable
    {
        private static int _nextId;
        public string Id { get; } = $"client-{Interlocked.Increment(ref _nextId)}";
        public string RemoteEndPoint { get; }

        private readonly TcpClient _client;
        private readonly CameraSimulatorServer _server;
        private readonly NetworkStream _stream;
        private readonly StreamReader _reader;
        private readonly StreamWriter _writer;
        private readonly SemaphoreSlim _writeLock = new(1, 1);
        private VirtualCameraState? _openCamera;
        private CancellationTokenSource? _streamCts;
        private bool _disposed;
        private bool _isStreaming;
        private int _slotIndex;

        public bool IsStreaming => _isStreaming;
        public string? OpenCameraSerial => _openCamera?.SerialNumber;

        public ClientSession(TcpClient client, CameraSimulatorServer server)
        {
            _client = client;
            _server = server;
            _stream = client.GetStream();
            _reader = new StreamReader(_stream, Encoding.ASCII, leaveOpen: true);
            _writer = new StreamWriter(_stream, Encoding.ASCII, leaveOpen: true) { AutoFlush = true };
            RemoteEndPoint = client.Client.RemoteEndPoint?.ToString() ?? "unknown";
        }

        public async Task RunAsync(CancellationToken serverCt)
        {
            try
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(serverCt);
                while (!linked.Token.IsCancellationRequested)
                {
                    string? line = await _reader.ReadLineAsync(linked.Token);
                    if (line == null) break;

                    string command = line.TrimEnd('\r', '\n');
                    if (string.IsNullOrWhiteSpace(command)) continue;
                    await HandleCommandAsync(command, linked.Token);
                }
            }
            catch (OperationCanceledException) { }
            catch (IOException) { }
            catch (Exception ex)
            {
                _server.Log($"Session {Id} error: {ex.Message}");
            }
            finally
            {
                StopStreaming();
                _server.RemoveSession(Id);
                _server.Log($"Client disconnected: {RemoteEndPoint} ({Id})");
            }
        }

        private async Task HandleCommandAsync(string command, CancellationToken ct)
        {
            int sp = command.IndexOf(' ');
            string cmd = sp < 0 ? command.ToUpperInvariant() : command.Substring(0, sp).ToUpperInvariant();
            string? remainder = sp < 0 ? null : command.Substring(sp + 1);

            switch (cmd)
            {
                case CameraProtocol.CmdList:
                    await SendListAsync();
                    break;
                case CameraProtocol.CmdOpen:
                    await HandleOpenAsync(remainder);
                    break;
                case CameraProtocol.CmdStart:
                    await HandleStartAsync(ct);
                    break;
                case CameraProtocol.CmdStop:
                    StopStreaming();
                    await SendAsync(CameraProtocol.RespOk);
                    break;
                case CameraProtocol.CmdClose:
                    StopStreaming();
                    _openCamera = null;
                    await SendAsync(CameraProtocol.RespOk);
                    break;
                case CameraProtocol.CmdPing:
                    await SendAsync(CameraProtocol.RespPong);
                    break;
                case CameraProtocol.CmdGetFeatures:
                    await HandleGetFeaturesAsync(remainder);
                    break;
                case CameraProtocol.CmdGetConfig:
                    await HandleGetConfigAsync(remainder);
                    break;
                case CameraProtocol.CmdSetConfig:
                    await HandleSetConfigAsync(remainder);
                    break;
                case CameraProtocol.CmdGetStatus:
                    await HandleGetStatusAsync(remainder);
                    break;
                default:
                    await SendAsync($"{CameraProtocol.RespError} unknown command: {cmd}");
                    break;
            }
        }

        private async Task SendListAsync()
        {
            var infos = _server.Cameras
                .Where(c => c.Enabled)
                .Select(c => c.ToInfo())
                .ToArray();
            string json = JsonSerializer.Serialize(infos);
            await SendAsync(json);
        }

        private async Task HandleOpenAsync(string? remainder)
        {
            if (string.IsNullOrWhiteSpace(remainder))
            {
                await SendAsync($"{CameraProtocol.RespError} usage: OPEN <serial>");
                return;
            }

            string serial = remainder.Trim();
            var cam = _server.GetCamera(serial);
            if (cam == null || !cam.Enabled)
            {
                await SendAsync($"{CameraProtocol.RespError} camera not found: {serial}");
                return;
            }

            _slotIndex = ParseSlotIndex(serial);
            cam.EnsureGenerator(_slotIndex);
            _openCamera = cam;
            await SendAsync(CameraProtocol.RespOk);
            _server.Log($"{Id} opened {serial}");
        }

        private static int ParseSlotIndex(string serial)
        {
            if (serial.StartsWith("SIM-CAM-", StringComparison.OrdinalIgnoreCase) &&
                serial.Length >= "SIM-CAM-01".Length &&
                int.TryParse(serial.AsSpan(serial.Length - 2), out int idx))
            {
                return Math.Clamp(idx - 1, 0, 3);
            }
            return 0;
        }

        private async Task HandleStartAsync(CancellationToken ct)
        {
            if (_openCamera == null)
            {
                await SendAsync($"{CameraProtocol.RespError} no camera opened");
                return;
            }

            if (_isStreaming)
            {
                await SendAsync($"{CameraProtocol.RespError} already streaming");
                return;
            }

            _openCamera.EnsureGenerator(_slotIndex);
            _openCamera.IsStreaming = true;
            _server.OnCameraStateChanged(_openCamera);
            _streamCts = new CancellationTokenSource();
            _isStreaming = true;
            await SendAsync(CameraProtocol.RespOk);
            _ = StreamFramesAsync(_openCamera, _streamCts.Token);
            _server.Log($"{Id} started streaming {_openCamera.SerialNumber}");
        }

        private void StopStreaming()
        {
            if (_streamCts != null)
            {
                _streamCts.Cancel();
                _streamCts.Dispose();
                _streamCts = null;
            }
            _isStreaming = false;
            if (_openCamera != null)
            {
                _openCamera.IsStreaming = _server.IsCameraStreamingByAnySession(_openCamera.SerialNumber);
                if (!_openCamera.IsStreaming)
                {
                    _openCamera.ActualFps = 0;
                }
                _server.OnCameraStateChanged(_openCamera);
            }
        }

        private async Task HandleGetFeaturesAsync(string? remainder)
        {
            if (string.IsNullOrWhiteSpace(remainder))
            {
                await SendAsync($"{CameraProtocol.RespError} usage: GET_FEATURES <serial>");
                return;
            }

            string serial = remainder.Trim();
            var cam = _server.GetCamera(serial);
            if (cam == null)
            {
                await SendAsync($"{CameraProtocol.RespError} camera not found: {serial}");
                return;
            }

            var caps = CameraConfigurationValidator.GetCapabilities();
            string json = JsonSerializer.Serialize(caps);
            await SendAsync(json);
        }

        private async Task HandleGetConfigAsync(string? remainder)
        {
            if (string.IsNullOrWhiteSpace(remainder))
            {
                await SendAsync($"{CameraProtocol.RespError} usage: GET_CONFIG <serial>");
                return;
            }

            string serial = remainder.Trim();
            var cam = _server.GetCamera(serial);
            if (cam == null)
            {
                await SendAsync($"{CameraProtocol.RespError} camera not found: {serial}");
                return;
            }

            var config = new
            {
                width = cam.Width,
                height = cam.Height,
                fps = cam.Fps,
                pixelFormat = CameraProtocol.PixelFormatBgr24,
                testPattern = cam.TestPattern,
            };
            string json = JsonSerializer.Serialize(config);
            await SendAsync(json);
        }

        private async Task HandleSetConfigAsync(string? remainder)
        {
            if (string.IsNullOrWhiteSpace(remainder))
            {
                await SendAsync($"{CameraProtocol.RespError} usage: SET_CONFIG <serial> <json>");
                return;
            }

            int sp = remainder.IndexOf(' ');
            if (sp < 0)
            {
                await SendAsync($"{CameraProtocol.RespError} usage: SET_CONFIG <serial> <json>");
                return;
            }

            string serial = remainder.Substring(0, sp).Trim();
            string jsonPayload = remainder.Substring(sp + 1).Trim();

            var cam = _server.GetCamera(serial);
            if (cam == null)
            {
                await SendAsync($"{CameraProtocol.RespError} camera not found: {serial}");
                return;
            }

            if (_server.IsCameraStreamingByAnySession(serial))
            {
                await SendAsync($"{CameraProtocol.RespError} camera is streaming; stop before changing config");
                return;
            }

            SetConfigPayload? payload;
            try
            {
                payload = JsonSerializer.Deserialize<SetConfigPayload>(jsonPayload);
            }
            catch
            {
                await SendAsync($"{CameraProtocol.RespError} invalid JSON");
                return;
            }

            if (payload == null)
            {
                await SendAsync($"{CameraProtocol.RespError} invalid config payload");
                return;
            }

            int newWidth = payload.Width ?? cam.Width;
            int newHeight = payload.Height ?? cam.Height;
            double newFps = payload.Fps ?? cam.Fps;
            string newPattern = payload.TestPattern ?? cam.TestPattern;

            var config = new CameraConfiguration(
                newWidth, newHeight, newFps,
                SimulatedPixelFormat.Bgr24, newPattern);

            string? error = CameraConfigurationValidator.Validate(config);
            if (error != null)
            {
                await SendAsync($"{CameraProtocol.RespError} {error}");
                return;
            }

            lock (cam.SyncRoot)
            {
                cam.Width = newWidth;
                cam.Height = newHeight;
                cam.Fps = (int)Math.Round(newFps);
                cam.TestPattern = newPattern;
                cam.Generator = null;
            }

            _server.OnCameraStateChanged(cam);

            var applied = new
            {
                width = cam.Width,
                height = cam.Height,
                fps = cam.Fps,
                pixelFormat = CameraProtocol.PixelFormatBgr24,
                testPattern = cam.TestPattern,
            };
            await SendAsync(JsonSerializer.Serialize(applied));
            _server.Log($"{Id} set config on {serial}: {cam.Width}x{cam.Height} @ {cam.Fps}fps pattern={cam.TestPattern}");
        }

        private async Task HandleGetStatusAsync(string? remainder)
        {
            string serial = remainder?.Trim() ?? string.Empty;

            if (string.IsNullOrEmpty(serial))
            {
                var statuses = _server.Cameras
                    .Select(c => new
                    {
                        serial = c.SerialNumber,
                        enabled = c.Enabled,
                        streaming = c.IsStreaming,
                        framesSent = c.FramesSent,
                        actualFps = Math.Round(c.ActualFps, 2),
                    })
                    .ToArray();
                await SendAsync(JsonSerializer.Serialize(statuses));
                return;
            }

            var cam = _server.GetCamera(serial);
            if (cam == null)
            {
                await SendAsync($"{CameraProtocol.RespError} camera not found: {serial}");
                return;
            }

            var single = new
            {
                serial = cam.SerialNumber,
                enabled = cam.Enabled,
                streaming = cam.IsStreaming,
                framesSent = cam.FramesSent,
                actualFps = Math.Round(cam.ActualFps, 2),
            };
            await SendAsync(JsonSerializer.Serialize(single));
        }

        private async Task StreamFramesAsync(VirtualCameraState cam, CancellationToken ct)
        {
            var sw = Stopwatch.StartNew();
            long framesSent = 0;
            double frameIntervalMs = 1000.0 / Math.Max(1, cam.Fps);
            long lastFpsUpdate = 0;

            try
            {
                while (!ct.IsCancellationRequested && _isStreaming)
                {
                    double targetMs = framesSent * frameIntervalMs;
                    double elapsed = sw.Elapsed.TotalMilliseconds;
                    double waitMs = targetMs - elapsed;

                    if (waitMs > 0)
                    {
                        if (waitMs > 1)
                            await Task.Delay((int)waitMs, ct);
                        else
                        {
                            var spin = new SpinWait();
                            while (sw.Elapsed.TotalMilliseconds < targetMs && !ct.IsCancellationRequested)
                                spin.SpinOnce();
                        }
                    }

                    if (ct.IsCancellationRequested || !_isStreaming) break;

                    cam.EnsureGenerator(_slotIndex);
                    byte[] pixels = cam.Generator!.Generate();
                    byte[] packet = CameraProtocol.BuildFramePacket(
                        cam.Width, cam.Height, CameraProtocol.PixelFormatBgr24, pixels);

                    await _writeLock.WaitAsync(ct);
                    try
                    {
                        await _stream.WriteAsync(packet, 0, packet.Length, ct);
                    }
                    finally
                    {
                        _writeLock.Release();
                    }
                    framesSent++;

                    if (sw.ElapsedMilliseconds - lastFpsUpdate >= 500)
                    {
                        lock (cam.SyncRoot)
                        {
                            cam.ActualFps = framesSent * 1000.0 / sw.Elapsed.TotalMilliseconds;
                            cam.FramesSent = framesSent;
                        }
                        _server.OnCameraStateChanged(cam);
                        lastFpsUpdate = sw.ElapsedMilliseconds;
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (IOException) { }
            catch (Exception ex)
            {
                _server.Log($"Stream error for {cam.SerialNumber}: {ex.Message}");
            }
            finally
            {
                _isStreaming = false;
                lock (cam.SyncRoot)
                {
                    cam.FramesSent = framesSent;
                    cam.ActualFps = 0;
                }
                cam.IsStreaming = _server.IsCameraStreamingByAnySession(cam.SerialNumber);
                _server.OnCameraStateChanged(cam);
            }
        }

        private async Task SendAsync(string message)
        {
            try
            {
                await _writeLock.WaitAsync();
                try
                {
                    await _writer.WriteLineAsync(message.AsMemory(), default);
                    await _writer.FlushAsync();
                }
                finally
                {
                    _writeLock.Release();
                }
            }
            catch { }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            StopStreaming();
            try { _writer.Dispose(); } catch { }
            try { _reader.Dispose(); } catch { }
            try { _stream.Dispose(); } catch { }
            try { _client.Dispose(); } catch { }
        }
    }

    private sealed class SetConfigPayload
    {
        public int? Width { get; set; }
        public int? Height { get; set; }
        public double? Fps { get; set; }
        public string? TestPattern { get; set; }
    }
}