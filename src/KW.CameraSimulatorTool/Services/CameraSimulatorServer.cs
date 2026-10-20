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

    public event Action<string>? LogMessage;
    public event Action<VirtualCameraState>? CameraUpdated;

    public IReadOnlyCollection<VirtualCameraState> Cameras => _cameras.Values.ToList();

    public CameraSimulatorServer(IEnumerable<VirtualCameraState> cameras)
    {
        _cameras = new ConcurrentDictionary<string, VirtualCameraState>(
            cameras.ToDictionary(c => c.SerialNumber, c => c));
    }

    public bool IsRunning => _listener != null;
    public int Port => _port;

    public async Task StartAsync(int port)
    {
        if (_listener != null)
            throw new InvalidOperationException("Server is already running.");

        _port = port;
        _serverCts = new CancellationTokenSource();
        _listener = new TcpListener(IPAddress.Any, port);
        _listener.Start();
        Log($"Server started on port {port}.");

        await AcceptLoopAsync(_serverCts.Token);
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
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopAsync().Wait(TimeSpan.FromSeconds(2));
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
        private readonly byte[] _commandBuffer = new byte[256];
        private VirtualCameraState? _openCamera;
        private CancellationTokenSource? _streamCts;
        private bool _disposed;

        public ClientSession(TcpClient client, CameraSimulatorServer server)
        {
            _client = client;
            _server = server;
            _stream = client.GetStream();
            RemoteEndPoint = client.Client.RemoteEndPoint?.ToString() ?? "unknown";
        }

        public async Task RunAsync(CancellationToken serverCt)
        {
            try
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(serverCt);
                while (!linked.Token.IsCancellationRequested)
                {
                    int read = await _stream.ReadAsync(_commandBuffer, 0, _commandBuffer.Length, linked.Token);
                    if (read == 0) break;

                    string command = CameraProtocol.DecodeCommand(_commandBuffer, read);
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
            if (string.IsNullOrWhiteSpace(command)) return;

            string[] parts = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            string cmd = parts[0].ToUpperInvariant();

            switch (cmd)
            {
                case CameraProtocol.CmdList:
                    await SendListAsync();
                    break;
                case CameraProtocol.CmdOpen:
                    await HandleOpenAsync(parts);
                    break;
                case CameraProtocol.CmdStart:
                    HandleStart();
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

        private async Task HandleOpenAsync(string[] parts)
        {
            if (parts.Length < 2)
            {
                await SendAsync($"{CameraProtocol.RespError} usage: OPEN <serial>");
                return;
            }

            string serial = parts[1];
            var cam = _server.GetCamera(serial);
            if (cam == null || !cam.Enabled)
            {
                await SendAsync($"{CameraProtocol.RespError} camera not found: {serial}");
                return;
            }

            cam.Generator ??= new FrameGenerator(cam.Width, cam.Height, ParseSlotIndex(serial));
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

        private void HandleStart()
        {
            if (_openCamera == null)
            {
                _ = SendAsync($"{CameraProtocol.RespError} no camera opened");
                return;
            }

            if (_openCamera.IsStreaming)
            {
                _ = SendAsync($"{CameraProtocol.RespError} already streaming");
                return;
            }

            _openCamera.IsStreaming = true;
            _server.OnCameraStateChanged(_openCamera);
            _streamCts = new CancellationTokenSource();
            _ = SendAsync(CameraProtocol.RespOk);
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
            if (_openCamera != null)
            {
                _openCamera.IsStreaming = false;
                _openCamera.ActualFps = 0;
                _server.OnCameraStateChanged(_openCamera);
            }
        }

        private async Task StreamFramesAsync(VirtualCameraState cam, CancellationToken ct)
        {
            var sw = Stopwatch.StartNew();
            long framesSent = 0;
            double frameIntervalMs = 1000.0 / Math.Max(1, cam.Fps);
            long lastFpsUpdate = 0;

            try
            {
                while (!ct.IsCancellationRequested && cam.IsStreaming)
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

                    byte[] pixels = cam.Generator!.Generate();
                    byte[] packet = CameraProtocol.BuildFramePacket(
                        cam.Width, cam.Height, CameraProtocol.PixelFormatBgr24, pixels);

                    await _stream.WriteAsync(packet, 0, packet.Length, ct);
                    framesSent++;

                    if (sw.ElapsedMilliseconds - lastFpsUpdate >= 500)
                    {
                        cam.ActualFps = framesSent * 1000.0 / sw.Elapsed.TotalMilliseconds;
                        cam.FramesSent = framesSent;
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
                cam.IsStreaming = false;
                cam.FramesSent = framesSent;
                cam.ActualFps = 0;
                _server.OnCameraStateChanged(cam);
            }
        }

        private async Task SendAsync(string message)
        {
            try
            {
                byte[] data = CameraProtocol.EncodeResponse(message);
                await _stream.WriteAsync(data, 0, data.Length);
            }
            catch { }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            StopStreaming();
            try { _stream.Dispose(); } catch { }
            try { _client.Dispose(); } catch { }
        }
    }
}