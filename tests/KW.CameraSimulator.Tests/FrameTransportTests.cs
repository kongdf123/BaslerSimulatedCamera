using System.IO;
using KW.CameraSimulator.Protocol;

namespace KW.CameraSimulator.Tests;

public class FrameTransportTests
{
    [Fact]
    public void ValidBgr24_ReturnsExpectedByteCount()
    {
        int result = FrameTransport.ValidateBgr24PayloadLength(640, 480, 640 * 480 * 3);
        Assert.Equal(640 * 480 * 3, result);
    }

    [Fact]
    public void ZeroWidth_Rejected()
    {
        Assert.Throws<InvalidDataException>(() =>
            FrameTransport.ValidateBgr24PayloadLength(0, 480, 0));
    }

    [Fact]
    public void NegativeWidth_Rejected()
    {
        Assert.Throws<InvalidDataException>(() =>
            FrameTransport.ValidateBgr24PayloadLength(-1, 480, 0));
    }

    [Fact]
    public void ZeroHeight_Rejected()
    {
        Assert.Throws<InvalidDataException>(() =>
            FrameTransport.ValidateBgr24PayloadLength(640, 0, 0));
    }

    [Fact]
    public void WidthAboveMax_Rejected()
    {
        Assert.Throws<InvalidDataException>(() =>
            FrameTransport.ValidateBgr24PayloadLength(FrameTransport.MaxWidth + 1, 480, 0));
    }

    [Fact]
    public void HeightAboveMax_Rejected()
    {
        Assert.Throws<InvalidDataException>(() =>
            FrameTransport.ValidateBgr24PayloadLength(640, FrameTransport.MaxHeight + 1, 0));
    }

    [Fact]
    public void IntMaxWidth_CausesOverflowRejection()
    {
        Assert.Throws<InvalidDataException>(() =>
            FrameTransport.ValidateBgr24PayloadLength(int.MaxValue, int.MaxValue, int.MaxValue));
    }

    [Fact]
    public void PayloadSizeMismatch_Rejected()
    {
        Assert.Throws<InvalidDataException>(() =>
            FrameTransport.ValidateBgr24PayloadLength(640, 480, 100));
    }

    [Fact]
    public void PayloadExceedsMaxBytes_Rejected()
    {
        Assert.Throws<InvalidDataException>(() =>
            FrameTransport.ValidateBgr24PayloadLength(4096, 2160, FrameTransport.MaxPayloadBytes + 1));
    }

    [Fact]
    public void MaxBoundaryDimensions_AcceptedOrRejectedByPolicy()
    {
        int expected = 4096 * 2160 * 3;
        if (expected <= FrameTransport.MaxPayloadBytes)
        {
            int result = FrameTransport.ValidateBgr24PayloadLength(4096, 2160, expected);
            Assert.Equal(expected, result);
        }
        else
        {
            Assert.Throws<InvalidDataException>(() =>
                FrameTransport.ValidateBgr24PayloadLength(4096, 2160, expected));
        }
    }

    [Fact]
    public async Task ReadExactlyAsync_ReturnsAllBytes()
    {
        byte[] source = Enumerable.Range(0, 100).Select(i => (byte)i).ToArray();
        using var stream = new MemoryStream(source);
        using var cts = new CancellationTokenSource();

        var destination = new Memory<byte>(new byte[100]);
        await FrameTransport.ReadExactlyAsync(stream, destination, cts.Token);

        Assert.Equal(source, destination.Span.ToArray());
    }

    [Fact]
    public async Task ReadExactlyAsync_ConnectionClosed_ThrowsEndOfStream()
    {
        using var stream = new MemoryStream();
        using var cts = new CancellationTokenSource();

        var destination = new Memory<byte>(new byte[50]);
        await Assert.ThrowsAsync<EndOfStreamException>(() =>
            FrameTransport.ReadExactlyAsync(stream, destination, cts.Token));
    }

    [Fact]
    public void TryParseFrameHeader_RejectsZeroDimensions()
    {
        byte[] header = new byte[CameraProtocol.FrameHeaderSize];
        BitConverter.GetBytes(0).CopyTo(header, 0);
        BitConverter.GetBytes(480).CopyTo(header, 4);
        BitConverter.GetBytes(0).CopyTo(header, 8);
        BitConverter.GetBytes(640 * 480 * 3).CopyTo(header, 12);

        Assert.False(CameraProtocol.TryParseFrameHeader(header, out _, out _, out _, out _));
    }

    [Fact]
    public void TryParseFrameHeader_RejectsOversizedWidth()
    {
        byte[] header = new byte[CameraProtocol.FrameHeaderSize];
        BitConverter.GetBytes(100000).CopyTo(header, 0);
        BitConverter.GetBytes(480).CopyTo(header, 4);
        BitConverter.GetBytes(0).CopyTo(header, 8);
        BitConverter.GetBytes(100000 * 480 * 3).CopyTo(header, 12);

        Assert.False(CameraProtocol.TryParseFrameHeader(header, out _, out _, out _, out _));
    }

    [Fact]
    public void TryParseFrameHeader_RejectsPayloadMismatch()
    {
        byte[] header = new byte[CameraProtocol.FrameHeaderSize];
        BitConverter.GetBytes(640).CopyTo(header, 0);
        BitConverter.GetBytes(480).CopyTo(header, 4);
        BitConverter.GetBytes(0).CopyTo(header, 8);
        BitConverter.GetBytes(9999).CopyTo(header, 12);

        Assert.False(CameraProtocol.TryParseFrameHeader(header, out _, out _, out _, out _));
    }

    [Fact]
    public void TryParseFrameHeader_AcceptsValid()
    {
        byte[] header = new byte[CameraProtocol.FrameHeaderSize];
        BitConverter.GetBytes(640).CopyTo(header, 0);
        BitConverter.GetBytes(480).CopyTo(header, 4);
        BitConverter.GetBytes(CameraProtocol.PixelFormatBgr24).CopyTo(header, 8);
        BitConverter.GetBytes(640 * 480 * 3).CopyTo(header, 12);

        bool ok = CameraProtocol.TryParseFrameHeader(header, out int w, out int h, out int pf, out int bc);
        Assert.True(ok);
        Assert.Equal(640, w);
        Assert.Equal(480, h);
        Assert.Equal(CameraProtocol.PixelFormatBgr24, pf);
        Assert.Equal(640 * 480 * 3, bc);
    }

    [Fact]
    public void TryParseFrameHeader_ShortBuffer_ReturnsFalse()
    {
        byte[] header = new byte[8];
        Assert.False(CameraProtocol.TryParseFrameHeader(header, out _, out _, out _, out _));
        Assert.False(CameraProtocol.TryParseFrameHeader(null!, out _, out _, out _, out _));
    }
}