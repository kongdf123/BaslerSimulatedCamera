using System.Buffers.Binary;

namespace KW.CameraSimulator.Protocol;

public static class FrameTransport
{
    public const int MaxWidth = 4096;
    public const int MaxHeight = 2160;
    public const int MaxPayloadBytes = 64 * 1024 * 1024;

    public static async Task ReadExactlyAsync(
        Stream stream,
        Memory<byte> destination,
        CancellationToken cancellationToken = default)
    {
        while (!destination.IsEmpty)
        {
            int read = await stream.ReadAsync(destination, cancellationToken);

            if (read == 0)
                throw new EndOfStreamException(
                    "Connection closed during frame read.");

            destination = destination[read..];
        }
    }

    public static int ValidateBgr24PayloadLength(
        int width,
        int height,
        int payloadLength)
    {
        if (width <= 0 || width > MaxWidth)
            throw new InvalidDataException("Invalid frame width.");

        if (height <= 0 || height > MaxHeight)
            throw new InvalidDataException("Invalid frame height.");

        long expected = checked((long)width * height * 3L);

        if (expected > MaxPayloadBytes)
            throw new InvalidDataException("Frame payload exceeds limit.");

        if (payloadLength != expected)
            throw new InvalidDataException(
                "Frame payload length does not match dimensions.");

        return checked((int)expected);
    }
}