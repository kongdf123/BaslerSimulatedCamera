using System.Text;

namespace KW.CameraSimulator.Protocol;

public static class CameraProtocol
{
    public const int DefaultPort = 5270;
    public const int FramePortOffset = 1;
    public static int DefaultFramePort => DefaultPort + FramePortOffset;
    public const string DefaultHost = "127.0.0.1";

    public const string CmdList = "LIST";
    public const string CmdOpen = "OPEN";
    public const string CmdStart = "START";
    public const string CmdStop = "STOP";
    public const string CmdClose = "CLOSE";
    public const string CmdPing = "PING";
    public const string CmdGetFeatures = "GET_FEATURES";
    public const string CmdGetConfig = "GET_CONFIG";
    public const string CmdSetConfig = "SET_CONFIG";
    public const string CmdGetStatus = "GET_STATUS";

    public const string CmdFrameAuth = "FRAME_AUTH";

    public const string RespOk = "OK";
    public const string RespError = "ERROR";
    public const string RespPong = "PONG";

    public const int PixelFormatBgr24 = 0;

    public const int FrameHeaderSize = 16;

    public static byte[] EncodeCommand(string command)
    {
        return Encoding.ASCII.GetBytes(command + "\r\n");
    }

    public static string DecodeCommand(byte[] buffer, int count)
    {
        return Encoding.ASCII.GetString(buffer, 0, count).TrimEnd('\r', '\n');
    }

    public static byte[] EncodeResponse(string response)
    {
        return Encoding.ASCII.GetBytes(response + "\r\n");
    }

    public static byte[] BuildFramePacket(int width, int height, int pixelFormat, byte[] pixels)
    {
        var packet = new byte[FrameHeaderSize + pixels.Length];
        BitConverter.GetBytes(width).CopyTo(packet, 0);
        BitConverter.GetBytes(height).CopyTo(packet, 4);
        BitConverter.GetBytes(pixelFormat).CopyTo(packet, 8);
        BitConverter.GetBytes(pixels.Length).CopyTo(packet, 12);
        pixels.CopyTo(packet, FrameHeaderSize);
        return packet;
    }

    public static bool TryParseFrameHeader(byte[] header, out int width, out int height, out int pixelFormat, out int byteCount)
    {
        width = 0;
        height = 0;
        pixelFormat = 0;
        byteCount = 0;

        if (header == null || header.Length < FrameHeaderSize)
            return false;

        width = BitConverter.ToInt32(header, 0);
        height = BitConverter.ToInt32(header, 4);
        pixelFormat = BitConverter.ToInt32(header, 8);
        byteCount = BitConverter.ToInt32(header, 12);

        if (width <= 0 || height <= 0 || byteCount <= 0)
            return false;

        if (pixelFormat != PixelFormatBgr24)
            return false;

        try
        {
            FrameTransport.ValidateBgr24PayloadLength(width, height, byteCount);
        }
        catch
        {
            return false;
        }

        return true;
    }
}