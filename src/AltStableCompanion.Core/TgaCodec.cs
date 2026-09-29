namespace AltStableCompanion.Core;

/// <summary>
/// A TGA file this codec does not read. <see cref="Truncated"/> tells the two kinds apart: a
/// file that ends early may still be being written and is worth reading again; a header this
/// codec does not understand will say the same thing however often it is read.
/// </summary>
public sealed class TgaFormatException(string message, bool truncated = false) : Exception(message)
{
    public bool Truncated { get; } = truncated;
}

/// <summary>
/// The two TGA shapes this app meets, and nothing more.
///
/// READ: what WoW writes. Measured on 1.60.1.70009 - a screenshot header is
/// <c>00 00 0A ...</c>: image type 10, RLE true-colour, 32 bpp, descriptor 0x20
/// (top-left origin, 0 alpha bits). Pillow decoded that silently, which is why the
/// Python converter never mentions RLE. Types 2 and 10, 24 and 32 bpp, either origin.
///
/// WRITE: what the Roster is known to load - uncompressed type 2, 32 bpp, descriptor
/// 0x08 (bottom-left origin, 8 alpha bits), the layout Pillow wrote for every cutout
/// already in use.
/// </summary>
public static class TgaCodec
{
    /// <summary>
    /// More pixels than any screenshot has (two 8K screens side by side are 66 million). The
    /// header's two 16-bit sizes can multiply past what an int holds; a file claiming that is
    /// corrupt, and is refused before anything is allocated for it.
    /// </summary>
    public const int MaxPixels = 128 * 1024 * 1024;

    public static RgbaImage Read(string path)
    {
        using var s = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        return Read(s);
    }

    public static RgbaImage Read(Stream stream)
    {
        var header = ReadExactly(stream, 18);
        int idLength = header[0];
        int colorMapType = header[1];
        int imageType = header[2];
        int colorMapLength = header[5] | (header[6] << 8);
        int colorMapEntryBits = header[7];
        int width = header[12] | (header[13] << 8);
        int height = header[14] | (header[15] << 8);
        int bpp = header[16];
        int descriptor = header[17];

        if (imageType != 2 && imageType != 10)
        {
            throw new TgaFormatException($"TGA image type {imageType} is not true-colour (2 or 10)");
        }
        if (bpp != 24 && bpp != 32)
        {
            throw new TgaFormatException($"TGA depth {bpp} is not 24 or 32 bits");
        }
        if (width == 0 || height == 0)
        {
            throw new TgaFormatException("TGA with no pixels");
        }
        if ((long)width * height > MaxPixels)
        {
            throw new TgaFormatException($"TGA of {width} x {height} is larger than any screenshot");
        }

        // Skip the image ID and any colour map; true-colour files do not use it.
        var skip = idLength + (colorMapType == 1 ? colorMapLength * ((colorMapEntryBits + 7) / 8) : 0);
        if (skip > 0) ReadExactly(stream, skip);

        var bytesPer = bpp / 8;
        var alphaBits = descriptor & 0x0F;
        var useAlpha = bpp == 32 && alphaBits > 0;
        var topDown = (descriptor & 0x20) != 0;
        var rightToLeft = (descriptor & 0x10) != 0;
        var count = width * height;

        // Decode to a LINEAR stream of pixels first. RLE packets run across row
        // boundaries, so mapping to rows while decoding packets gets that wrong.
        var linear = new byte[count * 4];
        if (imageType == 2)
        {
            var raw = ReadExactly(stream, count * bytesPer);
            for (var i = 0; i < count; i++)
            {
                Store(raw, i * bytesPer, linear, i, bytesPer, useAlpha);
            }
        }
        else
        {
            var pixel = new byte[4];
            var i = 0;
            while (i < count)
            {
                var packet = ReadByte(stream);
                var run = (packet & 0x7F) + 1;
                if (i + run > count)
                {
                    throw new TgaFormatException("RLE packet runs past the end of the image");
                }
                if ((packet & 0x80) != 0)
                {
                    ReadInto(stream, pixel, bytesPer);
                    for (var k = 0; k < run; k++) Store(pixel, 0, linear, i + k, bytesPer, useAlpha);
                }
                else
                {
                    var raw = ReadExactly(stream, run * bytesPer);
                    for (var k = 0; k < run; k++) Store(raw, k * bytesPer, linear, i + k, bytesPer, useAlpha);
                }
                i += run;
            }
        }

        // THEN apply the origin.
        var img = new RgbaImage(width, height);
        for (var row = 0; row < height; row++)
        {
            var dstY = topDown ? row : height - 1 - row;
            for (var col = 0; col < width; col++)
            {
                var dstX = rightToLeft ? width - 1 - col : col;
                Buffer.BlockCopy(linear, (row * width + col) * 4, img.Pixels, img.Offset(dstX, dstY), 4);
            }
        }
        return img;
    }

    public static void Write(string path, RgbaImage img)
    {
        using var s = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        Write(s, img);
    }

    public static void Write(Stream stream, RgbaImage img)
    {
        var header = new byte[18];
        header[2] = 2;                                  // uncompressed true-colour
        header[12] = (byte)(img.Width & 0xFF);
        header[13] = (byte)(img.Width >> 8);
        header[14] = (byte)(img.Height & 0xFF);
        header[15] = (byte)(img.Height >> 8);
        header[16] = 32;
        header[17] = 0x08;                              // bottom-left origin, 8 alpha bits
        stream.Write(header);

        var row = new byte[img.Width * 4];
        for (var y = img.Height - 1; y >= 0; y--)       // bottom row first
        {
            for (var x = 0; x < img.Width; x++)
            {
                var src = img.Offset(x, y);
                var dst = x * 4;
                row[dst] = img.Pixels[src + 2];         // B
                row[dst + 1] = img.Pixels[src + 1];     // G
                row[dst + 2] = img.Pixels[src];         // R
                row[dst + 3] = img.Pixels[src + 3];     // A
            }
            stream.Write(row);
        }
    }

    // BGR(A) at src -> RGBA at linear pixel index. Alpha is 255 whenever the file
    // does not declare any: a screenshot's fourth byte is padding, not coverage.
    private static void Store(byte[] src, int at, byte[] linear, int index, int bytesPer, bool useAlpha)
    {
        var o = index * 4;
        linear[o] = src[at + 2];
        linear[o + 1] = src[at + 1];
        linear[o + 2] = src[at];
        linear[o + 3] = bytesPer == 4 && useAlpha ? src[at + 3] : (byte)255;
    }

    private static byte[] ReadExactly(Stream s, int n)
    {
        var buf = new byte[n];
        ReadInto(s, buf, n);
        return buf;
    }

    private static void ReadInto(Stream s, byte[] buf, int n)
    {
        var got = 0;
        while (got < n)
        {
            var r = s.Read(buf, got, n - got);
            if (r == 0) throw new TgaFormatException("TGA ends early - still being written?", truncated: true);
            got += r;
        }
    }

    private static int ReadByte(Stream s)
    {
        var b = s.ReadByte();
        if (b < 0) throw new TgaFormatException("TGA ends early - still being written?", truncated: true);
        return b;
    }
}
