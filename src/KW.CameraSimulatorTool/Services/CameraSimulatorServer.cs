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
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly ConcurrentDictionary<string, VirtualCameraState> _cameras;
    private readonly ConcurrentDictionary<string, ControlSession> _controlSessions = new();
    private readonly ConcurrentDictionary<string, FrameSession> _frameSessions = new();
    private readonly ConcurrentDictionary<string, PendingStream> _pendingStreams = new();

    private TcpListener? _controlListener;
    private TcpListener? _frameListener;
    private CancellationTokenSource? _serverCts;
    private Task? _controlAcceptTask;
    private Task? _frameAcceptTask;

    private readonly SemaphoreSlim _stopLock = new(1, 1);
    private int _port;
    private int _framePort;
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

    public bool IsRunning => _controlListener != null;
    public int Port => _port;
    public int FramePort => _framePort;
    public bool AllowRemoteConnections => _allowRemoteConnections;
    public int ConnectedClients => _controlSessions.Count;

    public void Start(int port, bool allowRemoteConnections = false)
    {
        if (_controlListener != null)
            throw new InvalidOperationException("Server is already running.");

        _port = port;
        _framePort = port + CameraProtocol.FramePortOffset;
        _allowRemoteConnections = allowRemoteConnections;
        _serverCts = new CancellationTokenSource();

        IPAddress address = allowRemoteConnections ? IPAddress.Any : IPAddress.Loopback;

        _controlListener = new TcpListener(address, port);
        _controlListener.Start();

        try
        {
            _frameListener = new TcpListener(address, _framePort);
            _frameListener.Start();
        }
        catch (SocketException ex)
        {
            _controlListener.Stop();
            _controlListener = null;
            _serverCts.Dispose();
            _serverCts = null;
            throw new InvalidOperationException(
                $"Frame port {_framePort} is already in use. Choose a different control port.", ex);
        }

        string bindInfo = allowRemoteConnections ? "0.0.0.0" : "127.0.0.1";
        Log($"TCP server listening on {bindInfo}:{port} (control) + {_framePort} (frame)");

        _controlAcceptTask = AcceptControlLoopAsync(_serverCts.Token);
        _frameAcceptTask = AcceptFrameLoopAsync(_serverCts.Token);
    }

    public Task StartAsync(int port, bool allowRemoteConnections = false)
    {
        Start(port, allowRemoteConnections);
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        await _stopLock.WaitAsync();
        try
        {
            if (_controlListener == null) return;

            _serverCts?.Cancel();

            var controlSessions = _controlSessions.Values.ToArray();
            var frameSessions = _frameSessions.Values.ToArray();

            try { _frameListener?.Stop(); } catch { }
            _frameListener = null;
            try { _controlListener.Stop(); } catch { }
            _controlListener = null;

            foreach (var session in frameSessions)
            {
                try { session.Dispose(); }
                catch (Exception ex) { Log($"Frame session shutdown: {ex.Message}"); }
            }

            foreach (var session in controlSessions)
            {
                try { session.Dispose(); }
                catch (Exception ex) { Log($"Control session shutdown: {ex.Message}"); }
            }

            _pendingStreams.Clear();

            if (_frameAcceptTask != null)
            {
                try { await _frameAcceptTask; }
                catch (OperationCanceledException) { }
                catch (SocketException) { }
                catch (Exception ex) { Log($"Frame accept loop exited: {ex.Message}"); }
            }

            if (_controlAcceptTask != null)
            {
                try { await _controlAcceptTask; }
                catch (OperationCanceledException) { }
                catch (SocketException) { }
                catch (Exception ex) { Log($"Control accept loop exited: {ex.Message}"); }
            }

            try { await Task.WhenAll(controlSessions.Select(s => s.Completion)); }
            catch (Exception ex) { Log($"Control session completion: {ex.Message}"); }

            try { await Task.WhenAll(frameSessions.Select(s => s.Completion)); }
            catch (Exception ex) { Log($"Frame session completion: {ex.Message}"); }

            _controlSessions.Clear();
            _frameSessions.Clear();
            ConnectionCountChanged?.Invoke(0);

            foreach (var cam in _cameras.Values)
            {
                cam.IsStreaming = false;
                cam.ActualFps = 0;
                cam.FramesSent = 0;
                CameraUpdated?.Invoke(cam);
            }

            _serverCts?.Dispose();
            _serverCts = null;
            _controlAcceptTask = null;
            _frameAcceptTask = null;

            Log("Server stopped; all sessions closed.");
        }
        finally
        {
            _stopLock.Release();
        }
    }

    private async Task AcceptControlLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _controlListener != null)
        {
            try
            {
                var client = await _controlListener.AcceptTcpClientAsync(ct);
                var session = new ControlSession(client, this);
                _controlSessions[session.Id] = session;
                ConnectionCountChanged?.Invoke(_controlSessions.Count);
                Log($"Control client connected: {session.RemoteEndPoint} ({session.Id})");
                session.Start(ct);
            }
            catch (OperationCanceledException) { break; }
            catch (SocketException) { break; }
            catch (Exception ex) { Log($"Control accept error: {ex.Message}"); }
        }
    }

    private async Task AcceptFrameLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _frameListener != null)
        {
            try
            {
                var client = await _frameListener.AcceptTcpClientAsync(ct);
                var session = new FrameSession(client, this);
                _frameSessions[session.Id] = session;
                Log($"Frame client connected: {session.RemoteEndPoint} ({session.Id})");
                session.Start(ct);
            }
            catch (OperationCanceledException) { break; }
            catch (SocketException) { break; }
            catch (Exception ex) { Log($"Frame accept error: {ex.Message}"); }
        }
    }

    internal bool TryRegisterPendingStream(string streamId, ControlSession controlSession, VirtualCameraState cam, int slotIndex)
    {
        var pending = new PendingStream(controlSession, cam, slotIndex);
        return _pendingStreams.TryAdd(streamId, pending);
    }

    internal PendingStream? CompleteFrameAuth(string streamId, FrameSession frameSession)
    {
        if (_pendingStreams.TryRemove(streamId, out var pending))
        {
            pending.FrameSession = frameSession;
            return pending;
        }
        return null;
    }

    internal void RemovePending(string streamId)
    {
        _pendingStreams.TryRemove(streamId, out _);
    }

    internal void CloseFrameForControl(string controlSessionId)
    {
        foreach (var kv in _frameSessions)
        {
            if (kv.Value.ControlSessionId == controlSessionId)
            {
                try { kv.Value.Dispose(); } catch { }
            }
        }
    }

    internal void RemoveControlSession(string id)
    {
        _controlSessions.TryRemove(id, out _);
        CloseFrameForControl(id);
        ConnectionCountChanged?.Invoke(_controlSessions.Count);
    }

    internal void RemoveFrameSession(string id)
    {
        _frameSessions.TryRemove(id, out _);
    }

    internal void NotifyControlStreamEnded(string? controlSessionId)
    {
        if (controlSessionId != null &&
            _controlSessions.TryGetValue(controlSessionId, out var ctrl))
        {
            ctrl.OnFrameSessionEnded();
        }
    }

    internal VirtualCameraState? GetCamera(string serial)
    {
        _cameras.TryGetValue(serial, out var cam);
        return cam;
    }

    internal bool IsCameraStreamingByAnySession(string serial)
    {
        return _controlSessions.Values.Any(s => s.IsStreaming && s.OpenCameraSerial == serial);
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

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { StopAsync().Wait(TimeSpan.FromSeconds(3)); } catch { }
        _serverCts?.Dispose();
        _stopLock.Dispose();
    }

    internal sealed class PendingStream
    {
        public ControlSession Control { get; }
        public VirtualCameraState Camera { get; }
        public int SlotIndex { get; }
        public FrameSession? FrameSession { get; set; }

        public PendingStream(ControlSession control, VirtualCameraState camera, int slotIndex)
        {
            Control = control;
            Camera = camera;
            SlotIndex = slotIndex;
        }
    }

    internal class ControlSession : IDisposable
    {
        private static int _nextId;
        public string Id { get; } = $"control-{Interlocked.Increment(ref _nextId)}";
        public string RemoteEndPoint { get; }

        private readonly TcpClient _client;
        private readonly CameraSimulatorServer _server;
        private readonly NetworkStream _stream;
        private readonly StreamReader _reader;
        private readonly StreamWriter _writer;
        private readonly SemaphoreSlim _writeLock = new(1, 1);
        private VirtualCameraState? _openCamera;
        private bool _disposed;
        private bool _isStreaming;
        private int _slotIndex;
        private string? _activeStreamId;

        public Task Completion { get; private set; } = Task.CompletedTask;
        public bool IsStreaming => _isStreaming;
        public string? OpenCameraSerial => _openCamera?.SerialNumber;

        public ControlSession(TcpClient client, CameraSimulatorServer server)
        {
            _client = client;
            _server = server;
            _stream = client.GetStream();
            _reader = new StreamReader(_stream, Encoding.ASCII, leaveOpen: true);
            _writer = new StreamWriter(_stream, Encoding.ASCII, leaveOpen: true) { AutoFlush = true };
            RemoteEndPoint = client.Client.RemoteEndPoint?.ToString() ?? "unknown";
        }

        public void Start(CancellationToken serverToken)
        {
            Completion = RunAsync(serverToken);
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
            catch (Exception ex) { _server.Log($"Session {Id} error: {ex.Message}"); }
            finally
            {
                StopStreaming();
                _server.RemoveControlSession(Id);
                _server.Log($"Control client disconnected: {RemoteEndPoint} ({Id})");
            }
        }

        private async Task HandleCommandAsync(string command, CancellationToken ct)
        {
            int sp = command.IndexOf(' ');
            string cmd = sp < 0 ? command.ToUpperInvariant() : command.Substring(0, sp).ToUpperInvariant();
            string? remainder = sp < 0 ? null : command.Substring(sp + 1);

            switch (cmd)
            {
                case CameraProtocol.CmdList: await SendListAsync(); break;
                case CameraProtocol.CmdOpen: await HandleOpenAsync(remainder); break;
                case CameraProtocol.CmdStart: await HandleStartAsync(ct); break;
                case CameraProtocol.CmdStop: StopStreaming(); await SendAsync(CameraProtocol.RespOk); break;
                case CameraProtocol.CmdClose: StopStreaming(); _openCamera = null; await SendAsync(CameraProtocol.RespOk); break;
                case CameraProtocol.CmdPing: await SendAsync(CameraProtocol.RespPong); break;
                case CameraProtocol.CmdGetFeatures: await HandleGetFeaturesAsync(remainder); break;
                case CameraProtocol.CmdGetConfig: await HandleGetConfigAsync(remainder); break;
                case CameraProtocol.CmdSetConfig: await HandleSetConfigAsync(remainder); break;
                case CameraProtocol.CmdGetStatus: await HandleGetStatusAsync(remainder); break;
                default: await SendAsync($"{CameraProtocol.RespError} unknown command: {cmd}"); break;
            }
        }

        private async Task SendListAsync()
        {
            var infos = _server.Cameras.Where(c => c.Enabled).Select(c => c.ToInfo()).ToArray();
            await SendAsync(JsonSerializer.Serialize(infos));
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

            string streamId = Guid.NewGuid().ToString("N");
            if (!_server.TryRegisterPendingStream(streamId, this, _openCamera, _slotIndex))
            {
                await SendAsync($"{CameraProtocol.RespError} could not start stream");
                return;
            }

            _activeStreamId = streamId;
            await SendAsync($"OK sid={streamId}");
            _server.Log($"{Id} started streaming {_openCamera.SerialNumber} — awaiting frame connection on port {_server.FramePort}");
        }

        public void MarkStreamingActive()
        {
            if (_openCamera == null) return;
            _openCamera.EnsureGenerator(_slotIndex);
            _openCamera.IsStreaming = true;
            _isStreaming = true;
            _server.OnCameraStateChanged(_openCamera);
        }

        private void StopStreaming()
        {
            string? id = _activeStreamId;
            StopStreamingCore();
            if (id != null)
                _server.CloseFrameForControl(Id);
        }

        internal void OnFrameSessionEnded()
        {
            StopStreamingCore();
        }

        private void StopStreamingCore()
        {
            if (_activeStreamId != null)
            {
                _server.RemovePending(_activeStreamId);
                _activeStreamId = null;
            }
            _isStreaming = false;
            if (_openCamera != null)
            {
                _openCamera.IsStreaming = _server.IsCameraStreamingByAnySession(_openCamera.SerialNumber);
                if (!_openCamera.IsStreaming) _openCamera.ActualFps = 0;
                _server.OnCameraStateChanged(_openCamera);
            }
        }

        internal VirtualCameraState? OpenCamera => _openCamera;
        internal int SlotIndex => _slotIndex;

        private async Task HandleGetFeaturesAsync(string? remainder)
        {
            if (string.IsNullOrWhiteSpace(remainder))
            {
                await SendAsync($"{CameraProtocol.RespError} usage: GET_FEATURES <serial>");
                return;
            }
            var cam = _server.GetCamera(remainder.Trim());
            if (cam == null)
            {
                await SendAsync($"{CameraProtocol.RespError} camera not found: {remainder.Trim()}");
                return;
            }
            var caps = CameraConfigurationValidator.GetCapabilities();
            await SendAsync(JsonSerializer.Serialize(caps));
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
            await SendAsync(JsonSerializer.Serialize(config));
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
            try { payload = JsonSerializer.Deserialize<SetConfigPayload>(jsonPayload, JsonOpts); }
            catch { await SendAsync($"{CameraProtocol.RespError} invalid JSON"); return; }
            if (payload == null) { await SendAsync($"{CameraProtocol.RespError} invalid config payload"); return; }

            int newWidth = payload.Width ?? cam.Width;
            int newHeight = payload.Height ?? cam.Height;
            double newFps = payload.Fps ?? cam.Fps;
            string newPattern = payload.TestPattern ?? cam.TestPattern;

            var config = new CameraConfiguration(newWidth, newHeight, newFps, SimulatedPixelFormat.Bgr24, newPattern);
            string? error = CameraConfigurationValidator.Validate(config);
            if (error != null) { await SendAsync($"{CameraProtocol.RespError} {error}"); return; }

            lock (cam.SyncRoot)
            {
                cam.Width = newWidth;
                cam.Height = newHeight;
                cam.Fps = newFps;
                cam.TestPattern = newPattern;
                cam.Generator = null;
            }

            _server.OnCameraStateChanged(cam);
            var applied = new { width = cam.Width, height = cam.Height, fps = cam.Fps, pixelFormat = CameraProtocol.PixelFormatBgr24, testPattern = cam.TestPattern };
            await SendAsync(JsonSerializer.Serialize(applied));
            _server.Log($"{Id} set config on {serial}: {cam.Width}x{cam.Height} @ {cam.Fps}fps pattern={cam.TestPattern}");
        }

        private async Task HandleGetStatusAsync(string? remainder)
        {
            string serial = remainder?.Trim() ?? string.Empty;
            if (string.IsNullOrEmpty(serial))
            {
                var statuses = _server.Cameras.Select(c => new
                {
                    serial = c.SerialNumber, enabled = c.Enabled, streaming = c.IsStreaming,
                    framesSent = c.FramesSent, actualFps = Math.Round(c.ActualFps, 2),
                }).ToArray();
                await SendAsync(JsonSerializer.Serialize(statuses));
                return;
            }
            var cam = _server.GetCamera(serial);
            if (cam == null) { await SendAsync($"{CameraProtocol.RespError} camera not found: {serial}"); return; }
            var single = new { serial = cam.SerialNumber, enabled = cam.Enabled, streaming = cam.IsStreaming, framesSent = cam.FramesSent, actualFps = Math.Round(cam.ActualFps, 2) };
            await SendAsync(JsonSerializer.Serialize(single));
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
                finally { _writeLock.Release(); }
            }
            catch { }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            try { _client.Close(); } catch { }
            try { _stream.Close(); } catch { }

            StopStreaming();

            try { _writer.Dispose(); } catch { }
            try { _reader.Dispose(); } catch { }
            try { _stream.Dispose(); } catch { }
            try { _client.Dispose(); } catch { }
        }
    }

    internal class FrameSession : IDisposable
    {
        private static int _nextId;
        public string Id { get; } = $"frame-{Interlocked.Increment(ref _nextId)}";
        public string RemoteEndPoint { get; }
        public string? ControlSessionId { get; private set; }

        private readonly TcpClient _client;
        private readonly CameraSimulatorServer _server;
        private readonly NetworkStream _stream;
        private readonly SemaphoreSlim _writeLock = new(1, 1);
        private CancellationTokenSource? _streamCts;
        private VirtualCameraState? _camera;
        private double _targetFps;
        private int _genWidth, _genHeight, _slotIndex;
        private bool _disposed;
        private bool _hasAuthed;

        public Task Completion { get; private set; } = Task.CompletedTask;

        public FrameSession(TcpClient client, CameraSimulatorServer server)
        {
            _client = client;
            _server = server;
            _stream = client.GetStream();
            RemoteEndPoint = client.Client.RemoteEndPoint?.ToString() ?? "unknown";
        }

        public void Start(CancellationToken serverToken)
        {
            Completion = RunAsync(serverToken);
        }

        private async Task RunAsync(CancellationToken serverCt)
        {
            try
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(serverCt);

                string? line = await ReadLineAsync(linked.Token);
                if (line == null) return;

                string command = line.TrimEnd('\r', '\n');
                if (string.IsNullOrWhiteSpace(command)) return;

                int sp = command.IndexOf(' ');
                string cmd = sp < 0 ? command.ToUpperInvariant() : command.Substring(0, sp).ToUpperInvariant();
                string? remainder = sp < 0 ? null : command.Substring(sp + 1);

                if (cmd != CameraProtocol.CmdFrameAuth || string.IsNullOrWhiteSpace(remainder))
                {
                    await SendAsciiAsync($"{CameraProtocol.RespError} usage: {CameraProtocol.CmdFrameAuth} <sid>");
                    return;
                }

                string streamId = remainder.Trim();
                var pending = _server.CompleteFrameAuth(streamId, this);
                if (pending == null)
                {
                    await SendAsciiAsync($"{CameraProtocol.RespError} invalid or expired stream token");
                    return;
                }

                ControlSessionId = pending.Control.Id;
                _camera = pending.Camera;
                _slotIndex = pending.SlotIndex;
                _targetFps = pending.Camera.Fps;
                _genWidth = pending.Camera.Width;
                _genHeight = pending.Camera.Height;
                _hasAuthed = true;

                await SendAsciiAsync(CameraProtocol.RespOk);

                pending.Control.MarkStreamingActive();
                _server.Log($"Frame session {Id} authed for stream {streamId} (control {pending.Control.Id}, camera {pending.Camera.SerialNumber})");

                _streamCts = new CancellationTokenSource();
                await StreamFramesAsync(_streamCts.Token);
            }
            catch (OperationCanceledException) { }
            catch (IOException) { }
            catch (Exception ex) { _server.Log($"Frame session {Id} error: {ex.Message}"); }
            finally
            {
                if (_hasAuthed && _camera != null)
                {
                    _camera.IsStreaming = _server.IsCameraStreamingByAnySession(_camera.SerialNumber);
                    if (!_camera.IsStreaming) _camera.ActualFps = 0;
                    _server.OnCameraStateChanged(_camera);
                }
                _server.NotifyControlStreamEnded(ControlSessionId);
                _server.RemoveFrameSession(Id);
                _server.Log($"Frame client disconnected: {RemoteEndPoint} ({Id})");
            }
        }

        private async Task StreamFramesAsync(CancellationToken ct)
        {
            if (_camera == null) return;

            double frameIntervalMs = 1000.0 / Math.Max(0.1, _targetFps);
            var sw = Stopwatch.StartNew();
            long framesSent = 0;
            long lastFpsUpdate = 0;

            try
            {
                while (!ct.IsCancellationRequested)
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

                    if (ct.IsCancellationRequested) break;

                    _camera.EnsureGenerator(_slotIndex);
                    byte[] pixels = _camera.Generator!.Generate();
                    byte[] packet = CameraProtocol.BuildFramePacket(
                        _genWidth, _genHeight, CameraProtocol.PixelFormatBgr24, pixels);

                    await _writeLock.WaitAsync(ct);
                    try { await _stream.WriteAsync(packet, 0, packet.Length, ct); }
                    finally { _writeLock.Release(); }
                    framesSent++;

                    if (sw.ElapsedMilliseconds - lastFpsUpdate >= 500)
                    {
                        lock (_camera.SyncRoot)
                        {
                            _camera.ActualFps = framesSent * 1000.0 / sw.Elapsed.TotalMilliseconds;
                            _camera.FramesSent = framesSent;
                        }
                        _server.OnCameraStateChanged(_camera);
                        lastFpsUpdate = sw.ElapsedMilliseconds;
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (IOException) { }
            catch (Exception ex) { _server.Log($"Frame stream error: {ex.Message}"); }
            finally
            {
                lock (_camera.SyncRoot)
                {
                    _camera.FramesSent = framesSent;
                    _camera.ActualFps = 0;
                }
                _server.OnCameraStateChanged(_camera);
            }
        }

        private async Task SendAsciiAsync(string message)
        {
            try
            {
                byte[] data = Encoding.ASCII.GetBytes(message + "\r\n");
                await _writeLock.WaitAsync();
                try { await _stream.WriteAsync(data, 0, data.Length); await _stream.FlushAsync(); }
                finally { _writeLock.Release(); }
            }
            catch { }
        }

        private async Task<string?> ReadLineAsync(CancellationToken ct)
        {
            var sb = new StringBuilder();
            var buffer = new byte[1];
            while (true)
            {
                int read = await _stream.ReadAsync(buffer, 0, 1, ct);
                if (read <= 0) return null;
                char c = (char)buffer[0];
                if (c == '\n') break;
                if (c != '\r') sb.Append(c);
            }
            return sb.ToString();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _streamCts?.Cancel();
            _streamCts?.Dispose();
            _streamCts = null;

            try { _client.Close(); } catch { }
            try { _stream.Close(); } catch { }
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