namespace AltStableCompanion.Core;

/// <summary>
/// An 8-bit RGBA image, top-down, row-major: pixel (x, y) starts at
/// <c>(y * Width + x) * 4</c>. Every other type in the pipeline speaks this.
/// </summary>
public sealed class RgbaImage
{
    public RgbaImage(int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), $"{width}x{height} is not an image");
        }
        Width = width;
        Height = height;
        Pixels = new byte[checked(width * height * 4)];
    }

    public int Width { get; }
    public int Height { get; }
    public byte[] Pixels { get; }

    public int Offset(int x, int y) => (y * Width + x) * 4;

    public (byte R, byte G, byte B, byte A) this[int x, int y]
    {
        get
        {
            var i = Offset(x, y);
            return (Pixels[i], Pixels[i + 1], Pixels[i + 2], Pixels[i + 3]);
        }
        set
        {
            var i = Offset(x, y);
            Pixels[i] = value.R;
            Pixels[i + 1] = value.G;
            Pixels[i + 2] = value.B;
            Pixels[i + 3] = value.A;
        }
    }

    /// <summary>A copy of a rectangle of this image.</summary>
    public RgbaImage Crop(int x, int y, int width, int height)
    {
        var dst = new RgbaImage(width, height);
        for (var row = 0; row < height; row++)
        {
            Buffer.BlockCopy(Pixels, Offset(x, y + row), dst.Pixels, dst.Offset(0, row), width * 4);
        }
        return dst;
    }

    /// <summary>The smallest rectangle holding every pixel with alpha above zero, or null.</summary>
    public (int X, int Y, int Width, int Height)? AlphaBounds()
    {
        int minX = Width, minY = Height, maxX = -1, maxY = -1;
        for (var y = 0; y < Height; y++)
        {
            var row = y * Width * 4;
            for (var x = 0; x < Width; x++)
            {
                if (Pixels[row + x * 4 + 3] == 0) continue;
                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
            }
        }
        return maxX < 0 ? null : (minX, minY, maxX - minX + 1, maxY - minY + 1);
    }
}
