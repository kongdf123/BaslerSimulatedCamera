using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using KW.CameraSimulator.Protocol;
using KW.CameraSimulatorTool.Models;
using KW.CameraSimulatorTool.Services;

namespace KW.CameraSimulator.Tests;

public class ProtocolIntegrationTests
{
    private const int TestPortBase = 5280;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private static List<VirtualCameraState> BuildCameras() =>
        VirtualCameraInfo.DefaultCameras.Select(info => new VirtualCameraState
        {
            SerialNumber = info.SerialNumber,
            DisplayName = info.DisplayName,
            Width = info.Width,
            Height = info.Height,
            Fps = info.Fps,
            Enabled = true,
            TestPattern = TestPatterns.ColorBars,
        }).ToList();

    [Fact]
    public async Task FullProtocolFlow_ReturnsValidFrames()
    {
        int port = TestPortBase + 0;
        var cameras = BuildCameras();
        var server = new CameraSimulatorServer(cameras);
        var serverTask = server.StartAsync(port);
        await Task.Delay(500);

        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync("127.0.0.1", port);
            using var stream = client.GetStream();

            Send(stream, "LIST");
            string listResp = ReadLine(stream);
            var infos = JsonSerializer.Deserialize<VirtualCameraInfo[]>(listResp, JsonOpts);
            Assert.NotNull(infos);
            Assert.Equal(4, infos!.Length);
            Assert.Contains(infos, i => i.SerialNumber == "SIM-CAM-01");

            Send(stream, "OPEN SIM-CAM-01");
            Assert.Equal("OK", ReadLine(stream));

            string sid = await StartAndGetSidAsync(stream);
            var (frameClient, frameStream) = await ConnectFramePortAsync(port, sid);
            using (frameClient)
            {
                for (int i = 0; i < 3; i++)
                {
                    byte[] header = ReadExact(frameStream, 16);
                    int width = BitConverter.ToInt32(header, 0);
                    int height = BitConverter.ToInt32(header, 4);
                    int byteCount = BitConverter.ToInt32(header, 12);

                    Assert.Equal(640, width);
                    Assert.Equal(480, height);
                    Assert.Equal(width * height * 3, byteCount);

                    byte[] pixels = ReadExact(frameStream, byteCount);
                    Assert.Equal(byteCount, pixels.Length);
                }
            }

            Send(stream, "STOP");
            Assert.Equal("OK", ReadLine(stream));

            Send(stream, "CLOSE");
            Assert.Equal("OK", ReadLine(stream));
        }
        finally
        {
            await server.StopAsync();
            await serverTask;
        }
    }

    [Fact]
    public async Task GetFeatures_ReturnsCapabilityStructure()
    {
        int port = TestPortBase + 1;
        var cameras = BuildCameras();
        var server = new CameraSimulatorServer(cameras);
        var serverTask = server.StartAsync(port);
        await Task.Delay(500);

        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync("127.0.0.1", port);
            using var stream = client.GetStream();

            Send(stream, "GET_FEATURES SIM-CAM-01");
            string resp = ReadLine(stream);

            var caps = JsonSerializer.Deserialize<CameraConfigurationCapabilities>(resp, JsonOpts);
            Assert.NotNull(caps);
            Assert.Equal(64, caps!.MinWidth);
            Assert.Equal(1920, caps.MaxWidth);
            Assert.Contains(TestPatterns.ColorBars, caps.SupportedTestPatterns);
            Assert.Contains(TestPatterns.MovingTarget, caps.SupportedTestPatterns);
        }
        finally
        {
            await server.StopAsync();
            await serverTask;
        }
    }

    [Fact]
    public async Task GetConfig_ReturnsActualCameraConfiguration()
    {
        int port = TestPortBase + 2;
        var cameras = BuildCameras();
        var server = new CameraSimulatorServer(cameras);
        var serverTask = server.StartAsync(port);
        await Task.Delay(500);

        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync("127.0.0.1", port);
            using var stream = client.GetStream();

            Send(stream, "GET_CONFIG SIM-CAM-01");
            string resp = ReadLine(stream);
            Assert.DoesNotContain("ERROR", resp);

            var cfg = JsonSerializer.Deserialize<RemoteConfigDto>(resp, JsonOpts);
            Assert.NotNull(cfg);
            Assert.Equal(640, cfg!.Width);
            Assert.Equal(480, cfg.Height);
            Assert.Equal(TestPatterns.ColorBars, cfg.TestPattern);
        }
        finally
        {
            await server.StopAsync();
            await serverTask;
        }
    }

    [Fact]
    public async Task SetConfig_UpdatesCameraState()
    {
        int port = TestPortBase + 3;
        var cameras = BuildCameras();
        var server = new CameraSimulatorServer(cameras);
        var serverTask = server.StartAsync(port);
        await Task.Delay(500);

        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync("127.0.0.1", port);
            using var stream = client.GetStream();

            string jsonPayload = JsonSerializer.Serialize(new
            {
                width = 1280,
                height = 720,
                fps = 30.0,
                testPattern = TestPatterns.Checkerboard,
            });

            Send(stream, $"SET_CONFIG SIM-CAM-01 {jsonPayload}");
            string resp = ReadLine(stream);
            Assert.DoesNotContain("ERROR", resp);

            var applied = JsonSerializer.Deserialize<RemoteConfigDto>(resp, JsonOpts);
            Assert.NotNull(applied);
            Assert.Equal(1280, applied!.Width);
            Assert.Equal(720, applied.Height);
            Assert.Equal(30, (int)applied.Fps);
            Assert.Equal(TestPatterns.Checkerboard, applied.TestPattern);

            Send(stream, "GET_CONFIG SIM-CAM-01");
            string readback = ReadLine(stream);
            var rb = JsonSerializer.Deserialize<RemoteConfigDto>(readback, JsonOpts);
            Assert.Equal(1280, rb!.Width);
            Assert.Equal(720, rb.Height);
            Assert.Equal(TestPatterns.Checkerboard, rb.TestPattern);
        }
        finally
        {
            await server.StopAsync();
            await serverTask;
        }
    }

    [Fact]
    public async Task SetConfig_RejectsStreamingCamera()
    {
        int port = TestPortBase + 4;
        var cameras = BuildCameras();
        var server = new CameraSimulatorServer(cameras);
        var serverTask = server.StartAsync(port);
        await Task.Delay(500);

        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync("127.0.0.1", port);
            using var stream = client.GetStream();

            Send(stream, "OPEN SIM-CAM-01");
            Assert.Equal("OK", ReadLine(stream));
            string sid = await StartAndGetSidAsync(stream);
            var (fc, fs) = await ConnectFramePortAsync(port, sid);
            using (fc)
            {
                await Task.Delay(100);

                string jsonPayload = JsonSerializer.Serialize(new { width = 1024, height = 768 });
                Send(stream, $"SET_CONFIG SIM-CAM-01 {jsonPayload}");
                string resp = ReadLine(stream);

                Assert.StartsWith("ERROR", resp);
            }

            Send(stream, "STOP");
            Assert.Equal("OK", ReadLine(stream));
        }
        finally
        {
            await server.StopAsync();
            await serverTask;
        }
    }

    [Fact]
    public async Task SetConfig_RejectsInvalidValues()
    {
        int port = TestPortBase + 5;
        var cameras = BuildCameras();
        var server = new CameraSimulatorServer(cameras);
        var serverTask = server.StartAsync(port);
        await Task.Delay(500);

        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync("127.0.0.1", port);
            using var stream = client.GetStream();

            string bad = JsonSerializer.Serialize(new { width = 10, height = 10 });
            Send(stream, $"SET_CONFIG SIM-CAM-01 {bad}");
            string resp = ReadLine(stream);
            Assert.StartsWith("ERROR", resp);
        }
        finally
        {
            await server.StopAsync();
            await serverTask;
        }
    }

    [Fact]
    public async Task SetConfig_NewResolutionIsReflectedInFrames()
    {
        int port = TestPortBase + 6;
        var cameras = BuildCameras();
        var server = new CameraSimulatorServer(cameras);
        var serverTask = server.StartAsync(port);
        await Task.Delay(500);

        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync("127.0.0.1", port);
            using var stream = client.GetStream();

            string jsonPayload = JsonSerializer.Serialize(new
            {
                width = 1280,
                height = 720,
                fps = 60.0,
                testPattern = TestPatterns.MovingTarget,
            });
            Send(stream, $"SET_CONFIG SIM-CAM-01 {jsonPayload}");
            Assert.DoesNotContain("ERROR", ReadLine(stream));

            Send(stream, "OPEN SIM-CAM-01");
            Assert.Equal("OK", ReadLine(stream));

            string sid = await StartAndGetSidAsync(stream);
            var (frameClient, frameStream) = await ConnectFramePortAsync(port, sid);
            using (frameClient)
            {
                await Task.Delay(150);

                byte[] header = ReadExact(frameStream, 16);
                int w = BitConverter.ToInt32(header, 0);
                int h = BitConverter.ToInt32(header, 4);
                int byteCount = BitConverter.ToInt32(header, 12);

                Assert.Equal(1280, w);
                Assert.Equal(720, h);
                Assert.Equal(1280 * 720 * 3, byteCount);

                byte[] pixels = ReadExact(frameStream, byteCount);
                Assert.Equal(1280 * 720 * 3, pixels.Length);
            }

            Send(stream, "STOP");
            Assert.Equal("OK", ReadLine(stream));
        }
        finally
        {
            await server.StopAsync();
            await serverTask;
        }
    }

    [Fact]
    public async Task MultipleClients_CanStreamIndependently()
    {
        int port = TestPortBase + 7;
        var cameras = BuildCameras();
        var server = new CameraSimulatorServer(cameras);
        var serverTask = server.StartAsync(port);
        await Task.Delay(500);

        try
        {
            using var client1 = new TcpClient();
            await client1.ConnectAsync("127.0.0.1", port);
            using var stream1 = client1.GetStream();

            using var client2 = new TcpClient();
            await client2.ConnectAsync("127.0.0.1", port);
            using var stream2 = client2.GetStream();

            Send(stream1, "OPEN SIM-CAM-01");
            Assert.Equal("OK", ReadLine(stream1));
            string sid1 = await StartAndGetSidAsync(stream1);
            var (fc1, fs1) = await ConnectFramePortAsync(port, sid1);

            Send(stream2, "OPEN SIM-CAM-02");
            Assert.Equal("OK", ReadLine(stream2));
            string sid2 = await StartAndGetSidAsync(stream2);
            var (fc2, fs2) = await ConnectFramePortAsync(port, sid2);

            using (fc1)
            using (fc2)
            {
                byte[] h1 = ReadExact(fs1, 16);
                byte[] h2 = ReadExact(fs2, 16);

                Assert.Equal(640, BitConverter.ToInt32(h1, 0));
                Assert.Equal(640, BitConverter.ToInt32(h2, 0));
            }
        }
        finally
        {
            await server.StopAsync();
            await serverTask;
        }
    }

    [Fact]
    public async Task MultipleClients_OneStopDoesNotAffectOther()
    {
        int port = TestPortBase + 8;
        var cameras = BuildCameras();
        var server = new CameraSimulatorServer(cameras);
        var serverTask = server.StartAsync(port);
        await Task.Delay(500);

        try
        {
            using var client1 = new TcpClient();
            await client1.ConnectAsync("127.0.0.1", port);
            using var stream1 = client1.GetStream();

            using var client2 = new TcpClient();
            await client2.ConnectAsync("127.0.0.1", port);
            using var stream2 = client2.GetStream();

            Send(stream1, "OPEN SIM-CAM-01");
            Assert.Equal("OK", ReadLine(stream1));
            string sid1 = await StartAndGetSidAsync(stream1);
            var (fc1, fs1) = await ConnectFramePortAsync(port, sid1);

            Send(stream2, "OPEN SIM-CAM-01");
            Assert.Equal("OK", ReadLine(stream2));
            string sid2 = await StartAndGetSidAsync(stream2);
            var (fc2, fs2) = await ConnectFramePortAsync(port, sid2);

            await Task.Delay(100);

            DrainOneFrame(fs1);
            DrainOneFrame(fs2);

            fc1.Dispose();

            Send(stream1, "STOP");
            Assert.Equal("OK", ReadLine(stream1));

            await Task.Delay(100);

            byte[] h2b = DrainOneFrame(fs2);
            Assert.Equal(640, BitConverter.ToInt32(h2b, 0));
            Assert.Equal(480, BitConverter.ToInt32(h2b, 4));

            fc2.Dispose();

            Send(stream2, "STOP");
            Assert.Equal("OK", ReadLine(stream2));
        }
        finally
        {
            await server.StopAsync();
            await serverTask;
        }
    }

    [Fact]
    public async Task RepeatedStartStop_CyclesCleanly()
    {
        int port = TestPortBase + 16;
        var cameras = BuildCameras();
        var server = new CameraSimulatorServer(cameras);

        for (int cycle = 0; cycle < 3; cycle++)
        {
            var serverTask = server.StartAsync(port);
            await Task.Delay(300);

            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync("127.0.0.1", port);
                using var stream = client.GetStream();

                Send(stream, "PING");
                Assert.Equal("PONG", ReadLine(stream));

                Send(stream, "OPEN SIM-CAM-01");
                Assert.Equal("OK", ReadLine(stream));

                string sid = await StartAndGetSidAsync(stream);
                var (fc, fs) = await ConnectFramePortAsync(port, sid);
                using (fc)
                {
                    DrainOneFrame(fs);
                }

                Send(stream, "STOP");
                Assert.Equal("OK", ReadLine(stream));

                Send(stream, "CLOSE");
                Assert.Equal("OK", ReadLine(stream));
            }
            finally
            {
                await server.StopAsync();
                await serverTask;
            }

            await Task.Delay(200);
        }
    }

    [Fact]
    public async Task ServerStop_WhileClientIsStreaming_ClosesFramePort()
    {
        int port = TestPortBase + 17;
        var cameras = BuildCameras();
        var server = new CameraSimulatorServer(cameras);
        var serverTask = server.StartAsync(port);
        await Task.Delay(300);

        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync("127.0.0.1", port);
            using var stream = client.GetStream();

            Send(stream, "OPEN SIM-CAM-01");
            Assert.Equal("OK", ReadLine(stream));
            string sid = await StartAndGetSidAsync(stream);
            var (fc, fs) = await ConnectFramePortAsync(port, sid);
            using (fc)
            {
                DrainOneFrame(fs);

                await server.StopAsync();
                await serverTask;

                var ex = Assert.Throws<IOException>(() => DrainOneFrame(fs));
                Assert.NotNull(ex);
            }
        }
        finally
        {
            await server.StopAsync();
            await serverTask;
        }
    }

    [Fact]
    public async Task ControlClientDisconnect_FramePortAutomaticallyClosed()
    {
        int port = TestPortBase + 18;
        var cameras = BuildCameras();
        var server = new CameraSimulatorServer(cameras);
        var serverTask = server.StartAsync(port);
        await Task.Delay(300);

        TcpClient frameClient;
        NetworkStream frameStream;

        try
        {
            using (var controlClient = new TcpClient())
            {
                await controlClient.ConnectAsync("127.0.0.1", port);
                using var controlStream = controlClient.GetStream();

                Send(controlStream, "OPEN SIM-CAM-01");
                Assert.Equal("OK", ReadLine(controlStream));
                string sid = await StartAndGetSidAsync(controlStream);

                frameClient = new TcpClient();
                await frameClient.ConnectAsync("127.0.0.1", port + 1);
                frameStream = frameClient.GetStream();
                Send(frameStream, $"FRAME_AUTH {sid}");
                string authResp = ReadLine(frameStream);
                Assert.Equal("OK", authResp);

                DrainOneFrame(frameStream);
            }

            await Task.Delay(200);

            var ex = Assert.Throws<IOException>(() => DrainOneFrame(frameStream));
            Assert.NotNull(ex);
        }
        finally
        {
            await server.StopAsync();
            await serverTask;
        }
    }

    [Fact]
    public async Task FrameClientDisconnect_ControlClientCanContinue()
    {
        int port = TestPortBase + 19;
        var cameras = BuildCameras();
        var server = new CameraSimulatorServer(cameras);
        var serverTask = server.StartAsync(port);
        await Task.Delay(300);

        try
        {
            using var controlClient = new TcpClient();
            await controlClient.ConnectAsync("127.0.0.1", port);
            using var controlStream = controlClient.GetStream();

            Send(controlStream, "OPEN SIM-CAM-01");
            Assert.Equal("OK", ReadLine(controlStream));
            string sid = await StartAndGetSidAsync(controlStream);

            using (var fc = new TcpClient())
            {
                await fc.ConnectAsync("127.0.0.1", port + 1);
                var fs = fc.GetStream();
                Send(fs, $"FRAME_AUTH {sid}");
                Assert.Equal("OK", ReadLine(fs));
                DrainOneFrame(fs);
            }

            await Task.Delay(200);

            Send(controlStream, "PING");
            Assert.Equal("PONG", ReadLine(controlStream));

            Send(controlStream, "START");
            string resp2 = ReadLine(controlStream);
            Assert.StartsWith("OK sid=", resp2);
            string sid2 = resp2[(resp2.IndexOf("sid=", StringComparison.Ordinal) + 4)..];

            using (var fc2 = new TcpClient())
            {
                await fc2.ConnectAsync("127.0.0.1", port + 1);
                var fs2 = fc2.GetStream();
                Send(fs2, $"FRAME_AUTH {sid2}");
                Assert.Equal("OK", ReadLine(fs2));
                DrainOneFrame(fs2);
            }

            Send(controlStream, "STOP");
            Assert.Equal("OK", ReadLine(controlStream));
        }
        finally
        {
            await server.StopAsync();
            await serverTask;
        }
    }

    [Fact]
    public async Task InvalidFrameAuth_TokenIsRejected()
    {
        int port = TestPortBase + 20;
        var cameras = BuildCameras();
        var server = new CameraSimulatorServer(cameras);
        var serverTask = server.StartAsync(port);
        await Task.Delay(300);

        try
        {
            using var fc = new TcpClient();
            await fc.ConnectAsync("127.0.0.1", port + 1);
            using var fs = fc.GetStream();
            Send(fs, "FRAME_AUTH 00000000-0000-0000-0000-000000000000");
            string resp = ReadLine(fs);
            Assert.StartsWith("ERROR", resp);

            using var fc2 = new TcpClient();
            await fc2.ConnectAsync("127.0.0.1", port + 1);
            using var fs2 = fc2.GetStream();
            Send(fs2, "BAD_CMD");
            string resp2 = ReadLine(fs2);
            Assert.StartsWith("ERROR", resp2);
        }
        finally
        {
            await server.StopAsync();
            await serverTask;
        }
    }

    [Fact]
    public async Task GetStatus_ReturnsStreamingState()
    {
        int port = TestPortBase + 9;
        var cameras = BuildCameras();
        var server = new CameraSimulatorServer(cameras);
        var serverTask = server.StartAsync(port);
        await Task.Delay(500);

        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync("127.0.0.1", port);
            using var stream = client.GetStream();

            Send(stream, "GET_STATUS");
            string resp = ReadLine(stream);
            Assert.DoesNotContain("ERROR", resp);
            var statuses = JsonSerializer.Deserialize<List<CameraStatusDto>>(resp, JsonOpts);
            Assert.NotNull(statuses);
            Assert.Equal(4, statuses!.Count);
            Assert.All(statuses, s => Assert.False(s.Streaming));

            Send(stream, "OPEN SIM-CAM-01");
            Assert.Equal("OK", ReadLine(stream));
            string sid = await StartAndGetSidAsync(stream);
            var (fc, fs) = await ConnectFramePortAsync(port, sid);
            using (fc)
            {
                await Task.Delay(100);

                Send(stream, "GET_STATUS SIM-CAM-01");
                string single = ReadLine(stream);
                var one = JsonSerializer.Deserialize<CameraStatusDto>(single, JsonOpts);
                Assert.NotNull(one);
                Assert.Equal("SIM-CAM-01", one!.Serial);
                Assert.True(one.Streaming);
            }

            Send(stream, "STOP");
            Assert.Equal("OK", ReadLine(stream));
        }
        finally
        {
            await server.StopAsync();
            await serverTask;
        }
    }

    [Fact]
    public async Task UnknownCamera_ReturnsStructuredError()
    {
        int port = TestPortBase + 10;
        var cameras = BuildCameras();
        var server = new CameraSimulatorServer(cameras);
        var serverTask = server.StartAsync(port);
        await Task.Delay(500);

        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync("127.0.0.1", port);
            using var stream = client.GetStream();

            Send(stream, "GET_FEATURES SIM-CAM-99");
            string resp = ReadLine(stream);
            Assert.StartsWith("ERROR", resp);

            Send(stream, "OPEN SIM-CAM-99");
            resp = ReadLine(stream);
            Assert.StartsWith("ERROR", resp);
        }
        finally
        {
            await server.StopAsync();
            await serverTask;
        }
    }

    [Fact]
    public async Task UnknownCommand_ReturnsError()
    {
        int port = TestPortBase + 11;
        var cameras = BuildCameras();
        var server = new CameraSimulatorServer(cameras);
        var serverTask = server.StartAsync(port);
        await Task.Delay(500);

        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync("127.0.0.1", port);
            using var stream = client.GetStream();

            Send(stream, "FOOBAR");
            string resp = ReadLine(stream);
            Assert.StartsWith("ERROR", resp);
        }
        finally
        {
            await server.StopAsync();
            await serverTask;
        }
    }

    [Fact]
    public async Task CombinedCommandsInOneWrite_AreProcessedIndependently()
    {
        int port = TestPortBase + 12;
        var cameras = BuildCameras();
        var server = new CameraSimulatorServer(cameras);
        var serverTask = server.StartAsync(port);
        await Task.Delay(500);

        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync("127.0.0.1", port);
            using var stream = client.GetStream();

            byte[] combined = Encoding.ASCII.GetBytes(
                "PING\r\nPING\r\nPING\r\n");
            stream.Write(combined, 0, combined.Length);
            stream.Flush();

            string r1 = ReadLine(stream);
            string r2 = ReadLine(stream);
            string r3 = ReadLine(stream);

            Assert.Equal("PONG", r1);
            Assert.Equal("PONG", r2);
            Assert.Equal("PONG", r3);
        }
        finally
        {
            await server.StopAsync();
            await serverTask;
        }
    }

    [Fact]
    public async Task DisconnectedClient_DoesNotCrashServer()
    {
        int port = TestPortBase + 13;
        var cameras = BuildCameras();
        var server = new CameraSimulatorServer(cameras);
        var serverTask = server.StartAsync(port);
        await Task.Delay(500);

        try
        {
            using var client1 = new TcpClient();
            await client1.ConnectAsync("127.0.0.1", port);
            using (var s1 = client1.GetStream())
            {
                Send(s1, "OPEN SIM-CAM-01");
                Assert.Equal("OK", ReadLine(s1));
                string sid = await StartAndGetSidAsync(s1);
                var (fc, _) = await ConnectFramePortAsync(port, sid);
                using (fc)
                {
                    await Task.Delay(100);
                }
            }

            await Task.Delay(200);

            using var client2 = new TcpClient();
            await client2.ConnectAsync("127.0.0.1", port);
            using var s2 = client2.GetStream();
            Send(s2, "PING");
            Assert.Equal("PONG", ReadLine(s2));
        }
        finally
        {
            await server.StopAsync();
            await serverTask;
        }
    }

    [Fact]
    public async Task MultipleCameras_ConcurrentStreaming()
    {
        int port = TestPortBase + 14;
        var cameras = BuildCameras();

        cameras[0].Width = 640; cameras[0].Height = 480;
        cameras[1].Width = 1024; cameras[1].Height = 768;
        cameras[2].Width = 800; cameras[2].Height = 600;
        cameras[3].Width = 1280; cameras[3].Height = 720;

        var server = new CameraSimulatorServer(cameras);
        var serverTask = server.StartAsync(port);
        await Task.Delay(500);

        try
        {
            var tasks = new List<Task<(int w, int h, int byteCount)>>();
            for (int i = 0; i < 4; i++)
            {
                tasks.Add(ReadFrameFromCameraAsync(port, $"SIM-CAM-0{i + 1}"));
            }

            var results = await Task.WhenAll(tasks);
            Assert.Equal(640 * 480 * 3, results[0].byteCount);
            Assert.Equal(1024 * 768 * 3, results[1].byteCount);
            Assert.Equal(800 * 600 * 3, results[2].byteCount);
            Assert.Equal(1280 * 720 * 3, results[3].byteCount);

            Assert.Equal(640, results[0].w);
            Assert.Equal(1024, results[1].w);
            Assert.Equal(800, results[2].w);
            Assert.Equal(1280, results[3].w);
        }
        finally
        {
            await server.StopAsync();
            await serverTask;
        }
    }

    private static async Task<(int w, int h, int byteCount)> ReadFrameFromCameraAsync(int port, string serial)
    {
        using var client = new TcpClient();
        await client.ConnectAsync("127.0.0.1", port);
        using var stream = client.GetStream();

        Send(stream, $"OPEN {serial}");
        Assert.Equal("OK", ReadLine(stream));
        string sid = await StartAndGetSidAsync(stream);
        var (frameClient, frameStream) = await ConnectFramePortAsync(port, sid);
        using (frameClient)
        {
            await Task.Delay(100);

            byte[] header = ReadExact(frameStream, 16);
            int w = BitConverter.ToInt32(header, 0);
            int h = BitConverter.ToInt32(header, 4);
            int bc = BitConverter.ToInt32(header, 12);

            return (w, h, bc);
        }
    }

    [Fact]
    public async Task ServerBindAddress_DefaultsToLoopback()
    {
        int port = TestPortBase + 15;
        var cameras = BuildCameras();
        var server = new CameraSimulatorServer(cameras);
        var serverTask = server.StartAsync(port);
        await Task.Delay(500);

        try
        {
            Assert.True(server.IsRunning);
            Assert.Equal(port, server.Port);
            Assert.False(server.AllowRemoteConnections);
        }
        finally
        {
            await server.StopAsync();
            await serverTask;
        }
    }

    [Fact]
    public async Task PartialConfig_PreservesOtherFields()
    {
        int port = TestPortBase + 16;
        var cameras = BuildCameras();
        var server = new CameraSimulatorServer(cameras);
        var serverTask = server.StartAsync(port);
        await Task.Delay(500);

        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync("127.0.0.1", port);
            using var stream = client.GetStream();

            Send(stream, "GET_CONFIG SIM-CAM-01");
            string original = ReadLine(stream);
            var orig = JsonSerializer.Deserialize<RemoteConfigDto>(original, JsonOpts);
            Assert.Equal(TestPatterns.ColorBars, orig!.TestPattern);
            Assert.Equal(100, (int)orig.Fps);

            string jsonPayload = JsonSerializer.Serialize(new { width = 1024, height = 768 });
            Send(stream, $"SET_CONFIG SIM-CAM-01 {jsonPayload}");
            Assert.DoesNotContain("ERROR", ReadLine(stream));

            Send(stream, "GET_CONFIG SIM-CAM-01");
            string after = ReadLine(stream);
            var aft = JsonSerializer.Deserialize<RemoteConfigDto>(after, JsonOpts);
            Assert.Equal(1024, aft!.Width);
            Assert.Equal(768, aft.Height);
            Assert.Equal(100, (int)aft.Fps);
            Assert.Equal(TestPatterns.ColorBars, aft.TestPattern);
        }
        finally
        {
            await server.StopAsync();
            await serverTask;
        }
    }

    private static void Send(NetworkStream stream, string command)
    {
        byte[] data = Encoding.ASCII.GetBytes(command + "\r\n");
        stream.Write(data, 0, data.Length);
        stream.Flush();
    }

    private static string ReadLine(NetworkStream stream)
    {
        var sb = new StringBuilder();
        var buffer = new byte[1];
        stream.ReadTimeout = 5000;
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

    private static byte[] ReadExact(NetworkStream stream, int count)
    {
        byte[] buffer = new byte[count];
        int offset = 0;
        stream.ReadTimeout = 10000;
        while (offset < count)
        {
            int read = stream.Read(buffer, offset, count - offset);
            if (read <= 0) throw new IOException("Connection closed");
            offset += read;
        }
        return buffer;
    }

    private static byte[] DrainOneFrame(NetworkStream stream)
    {
        byte[] header = ReadExact(stream, 16);
        int byteCount = BitConverter.ToInt32(header, 12);
        ReadExact(stream, byteCount);
        return header;
    }

    private static async Task<string> StartAndGetSidAsync(NetworkStream controlStream)
    {
        Send(controlStream, "START");
        string resp = ReadLine(controlStream);
        Assert.StartsWith("OK", resp);
        int eq = resp.IndexOf("sid=", StringComparison.Ordinal);
        Assert.True(eq > 0, $"START response missing sid: {resp}");
        return resp[(eq + 4)..];
    }

    private static async Task<(TcpClient frameClient, NetworkStream frameStream)> ConnectFramePortAsync(int controlPort, string sid)
    {
        var frameClient = new TcpClient();
        await frameClient.ConnectAsync("127.0.0.1", controlPort + 1);
        var frameStream = frameClient.GetStream();
        Send(frameStream, $"FRAME_AUTH {sid}");
        string authResp = ReadLine(frameStream);
        Assert.Equal("OK", authResp);
        return (frameClient, frameStream);
    }

    private sealed class RemoteConfigDto
    {
        public int Width { get; set; }
        public int Height { get; set; }
        public double Fps { get; set; }
        public int PixelFormat { get; set; }
        public string TestPattern { get; set; } = string.Empty;
    }

    private sealed class CameraStatusDto
    {
        public string Serial { get; set; } = string.Empty;
        public bool Enabled { get; set; }
        public bool Streaming { get; set; }
        public long FramesSent { get; set; }
        public double ActualFps { get; set; }
    }
}