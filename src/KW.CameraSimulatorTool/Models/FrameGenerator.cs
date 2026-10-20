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
    public bool ReverseX { get; set; }

    public FrameGenerator(int width, int height, int slotIndex)
    {
        Width = width;
        Height = height;
        _slotIndex = slotIndex;
        _baseR = (byte)(40 + (slotIndex * 40));
        _baseG = (byte)(80 + (slotIndex * 20));
        _baseB = (byte)(120 + (slotIndex * 10));
    }

    public byte[] Generate()
    {
        int byteCount = Width * Height * 3;
        byte[] buffer = new byte[byteCount];
        long frameIndex = Interlocked.Increment(ref _frameCounter);
        int band = (int)(frameIndex % Width);

        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                int drawX = ReverseX ? Width - 1 - x : x;
                int offset = ((y * Width) + drawX) * 3;
                byte shade = (byte)((x + band) % 256);
                buffer[offset + 0] = (byte)((_baseB + shade) % 256);
                buffer[offset + 1] = (byte)((_baseG + (shade / 2)) % 256);
                buffer[offset + 2] = (byte)((_baseR + (y % 64)) % 256);
            }
        }

        return buffer;
    }
}