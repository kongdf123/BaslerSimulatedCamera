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
    private const int TestPort = 5271;

    [Fact]
    public async Task FullProtocolFlow_ReturnsValidFrames()
    {
        var cameras = VirtualCameraInfo.DefaultCameras.Select(info => new VirtualCameraState
        {
            SerialNumber = info.SerialNumber,
            DisplayName = info.DisplayName,
            Width = info.Width,
            Height = info.Height,
            Fps = info.Fps,
            Enabled = true,
        }).ToList();

        var server = new CameraSimulatorServer(cameras);
        var serverTask = server.StartAsync(TestPort);

        await Task.Delay(500);

        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync("127.0.0.1", TestPort);
            using var stream = client.GetStream();

            // LIST
            Send(stream, "LIST");
            string listResp = ReadLine(stream);
            var infos = JsonSerializer.Deserialize<VirtualCameraInfo[]>(listResp);
            Assert.NotNull(infos);
            Assert.Equal(4, infos!.Length);
            Assert.Contains(infos, i => i.SerialNumber == "SIM-CAM-01");

            // OPEN
            Send(stream, "OPEN SIM-CAM-01");
            string openResp = ReadLine(stream);
            Assert.Equal("OK", openResp);

            // START
            Send(stream, "START");
            string startResp = ReadLine(stream);
            Assert.Equal("OK", startResp);

            // Read 3 frames
            for (int i = 0; i < 3; i++)
            {
                byte[] header = ReadExact(stream, 16);
                int width = BitConverter.ToInt32(header, 0);
                int height = BitConverter.ToInt32(header, 4);
                int pixelFormat = BitConverter.ToInt32(header, 8);
                int byteCount = BitConverter.ToInt32(header, 12);

                Assert.Equal(640, width);
                Assert.Equal(480, height);
                Assert.Equal(CameraProtocol.PixelFormatBgr24, pixelFormat);
                Assert.Equal(width * height * 3, byteCount);

                byte[] pixels = ReadExact(stream, byteCount);
                Assert.Equal(byteCount, pixels.Length);
            }

            // STOP
            Send(stream, "STOP");
            string stopResp = ReadLine(stream);
            Assert.Equal("OK", stopResp);

            // CLOSE
            Send(stream, "CLOSE");
            string closeResp = ReadLine(stream);
            Assert.Equal("OK", closeResp);
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
        var cameras = VirtualCameraInfo.DefaultCameras.Select(info => new VirtualCameraState
        {
            SerialNumber = info.SerialNumber,
            DisplayName = info.DisplayName,
            Width = info.Width,
            Height = info.Height,
            Fps = info.Fps,
            Enabled = true,
        }).ToList();

        var server = new CameraSimulatorServer(cameras);
        var serverTask = server.StartAsync(TestPort + 1);

        await Task.Delay(500);

        try
        {
            using var client1 = new TcpClient();
            await client1.ConnectAsync("127.0.0.1", TestPort + 1);
            using var stream1 = client1.GetStream();

            using var client2 = new TcpClient();
            await client2.ConnectAsync("127.0.0.1", TestPort + 1);
            using var stream2 = client2.GetStream();

            Send(stream1, "OPEN SIM-CAM-01");
            Assert.Equal("OK", ReadLine(stream1));
            Send(stream1, "START");
            Assert.Equal("OK", ReadLine(stream1));

            Send(stream2, "OPEN SIM-CAM-02");
            Assert.Equal("OK", ReadLine(stream2));
            Send(stream2, "START");
            Assert.Equal("OK", ReadLine(stream2));

            byte[] h1 = ReadExact(stream1, 16);
            byte[] h2 = ReadExact(stream2, 16);

            Assert.Equal(640, BitConverter.ToInt32(h1, 0));
            Assert.Equal(640, BitConverter.ToInt32(h2, 0));
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
}