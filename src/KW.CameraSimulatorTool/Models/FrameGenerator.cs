using KW.CameraSimulator.Protocol;

namespace KW.CameraSimulatorTool.Models;

public class FrameGenerator
{
    private int _frameCounter;
    private readonly int _slotIndex;
    private readonly byte _baseR;
    private readonly byte _baseG;
    private readonly byte _baseB;

    public int Width { get; }
    public int Height { get; }
    public string TestPattern { get; }

    public FrameGenerator(int width, int height, int slotIndex, string testPattern = TestPatterns.ColorBars)
    {
        Width = width;
        Height = height;
        _slotIndex = slotIndex;
        TestPattern = string.IsNullOrWhiteSpace(testPattern) ? TestPatterns.ColorBars : testPattern;
        _baseR = (byte)(40 + (slotIndex * 40));
        _baseG = (byte)(80 + (slotIndex * 20));
        _baseB = (byte)(120 + (slotIndex * 10));
    }

    public byte[] Generate()
    {
        return TestPattern switch
        {
            TestPatterns.Gradient => GenerateGradient(),
            TestPatterns.Checkerboard => GenerateCheckerboard(),
            TestPatterns.MovingTarget => GenerateMovingTarget(),
            _ => GenerateColorBars()
        };
    }

    private byte[] GenerateColorBars()
    {
        int byteCount = Width * Height * 3;
        byte[] buffer = new byte[byteCount];
        long frameIndex = Interlocked.Increment(ref _frameCounter);
        int band = (int)(frameIndex % Width);

        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                int offset = ((y * Width) + x) * 3;
                byte shade = (byte)((x + band) % 256);
                buffer[offset + 0] = (byte)((_baseB + shade) % 256);
                buffer[offset + 1] = (byte)((_baseG + (shade / 2)) % 256);
                buffer[offset + 2] = (byte)((_baseR + (y % 64)) % 256);
            }
        }

        return buffer;
    }

    private byte[] GenerateGradient()
    {
        int byteCount = Width * Height * 3;
        byte[] buffer = new byte[byteCount];
        long frameIndex = Interlocked.Increment(ref _frameCounter);

        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                int offset = ((y * Width) + x) * 3;
                byte v = (byte)(x * 255 / Math.Max(1, Width - 1));
                buffer[offset + 0] = (byte)((v + _baseB) % 256);
                buffer[offset + 1] = (byte)((v + _baseG) % 256);
                buffer[offset + 2] = (byte)(y * 255 / Math.Max(1, Height - 1));
            }
        }

        _ = frameIndex;
        return buffer;
    }

    private byte[] GenerateCheckerboard()
    {
        int byteCount = Width * Height * 3;
        byte[] buffer = new byte[byteCount];
        long frameIndex = Interlocked.Increment(ref _frameCounter);
        int tile = 32;

        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                int offset = ((y * Width) + x) * 3;
                bool dark = ((x / tile) + (y / tile)) % 2 == 0;
                byte v = dark ? (byte)40 : (byte)220;
                buffer[offset + 0] = (byte)((v + _baseB / 3) % 256);
                buffer[offset + 1] = (byte)((v + _baseG / 3) % 256);
                buffer[offset + 2] = (byte)((v + _baseR / 3) % 256);
            }
        }

        _ = frameIndex;
        return buffer;
    }

    private byte[] GenerateMovingTarget()
    {
        int byteCount = Width * Height * 3;
        byte[] buffer = new byte[byteCount];
        long frameIndex = Interlocked.Increment(ref _frameCounter);

        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                int offset = ((y * Width) + x) * 3;
                buffer[offset + 0] = 16;
                buffer[offset + 1] = 16;
                buffer[offset + 2] = 16;
            }
        }

        int targetSize = Math.Min(64, Math.Min(Width, Height) / 4);
        int cx = Width / 2 + (int)(frameIndex * 3) % Math.Max(1, Width - targetSize) - (Width / 2);
        int cy = Height / 2 + (int)(frameIndex * 5) % Math.Max(1, Height - targetSize) - (Height / 2);
        cx = Math.Clamp(cx, 0, Math.Max(0, Width - targetSize));
        cy = Math.Clamp(cy, 0, Math.Max(0, Height - targetSize));

        for (int y = cy; y < cy + targetSize && y < Height; y++)
        {
            for (int x = cx; x < cx + targetSize && x < Width; x++)
            {
                int offset = ((y * Width) + x) * 3;
                int dx = x - cx;
                int dy = y - cy;
                int r = targetSize / 2;
                double d = Math.Sqrt(dx * dx + dy * dy);

                if (d <= r)
                {
                    buffer[offset + 0] = 30;
                    buffer[offset + 1] = 200;
                    buffer[offset + 2] = 30;

                    if (d > r - 4)
                    {
                        buffer[offset + 0] = 255;
                        buffer[offset + 1] = 255;
                        buffer[offset + 2] = 255;
                    }
                }
            }
        }

        return buffer;
    }
}