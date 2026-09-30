using System.Buffers.Binary;
using System.IO.Compression;

namespace AltStableCompanion.Core;

public sealed class PngFormatException(string message) : Exception(message);

/// <summary>
/// The PNG subset the enhancement needs, hand-rolled like <see cref="TgaCodec"/>: what
/// Codex's image tool writes (8-bit RGBA, non-interlaced) and what we hand it (the same).
/// Read takes 8-bit RGB and RGBA, non-interlaced, all five filters, with <c>tRNS</c> on RGB
/// as alpha and IDAT split across chunks; every chunk's CRC is checked. Anything else -
/// interlace, 16-bit, palette, grey, an unknown critical chunk - is refused by name. The
/// limits are the enhancement's, not the screenshots': generated pictures are a couple of
/// megapixels, and the resampler spends four doubles per source pixel.
/// </summary>
public static class PngCodec
{
    public const int MaxSide = 4096;
    public const int MaxPixels = 12 * 1024 * 1024;
    public const int MaxEncodedBytes = 32 * 1024 * 1024;

    private static readonly byte[] Signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    public static RgbaImage Read(string path)
    {
        using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        // The limit is enforced before any allocation, and the read is bounded whatever the
        // file does while it is open: at most MaxEncodedBytes + 1 bytes are ever read.
        if (s.Length > MaxEncodedBytes) throw new PngFormatException($"{s.Length} bytes: larger than a generated picture ({MaxEncodedBytes})");
        var bytes = new byte[(int)s.Length];
        var got = 0;
        while (got < bytes.Length)
        {
            var n = s.Read(bytes, got, bytes.Length - got);
            if (n == 0) throw new PngFormatException($"the file shrank while it was read: {got} of {bytes.Length} bytes");
            got += n;
        }
        if (s.ReadByte() != -1) throw new PngFormatException("the file grew while it was read");
        return Read(bytes);
    }

    public static RgbaImage Read(ReadOnlySpan<byte> png)
    {
        if (png.Length > MaxEncodedBytes) throw new PngFormatException($"{png.Length} bytes: larger than a generated picture ({MaxEncodedBytes})");
        if (png.Length < 8 || !png[..8].SequenceEqual(Signature)) throw new PngFormatException("not a PNG (no signature)");

        int width = 0, height = 0, channels = 0;
        (byte R, byte G, byte B)? colourKey = null;
        var idat = new MemoryStream();
        var seenHeader = false;
        var seenEnd = false;
        var pos = 8;
        while (!seenEnd)
        {
            if (pos + 12 > png.Length) throw new PngFormatException("the file ends inside a chunk header");
            var length = BinaryPrimitives.ReadUInt32BigEndian(png[pos..]);
            if ((long)pos + 12 + length > png.Length) throw new PngFormatException("a chunk runs past the end of the file");
            var type = png.Slice(pos + 4, 4);
            var data = png.Slice(pos + 8, (int)length);
            var crc = BinaryPrimitives.ReadUInt32BigEndian(png[(pos + 8 + (int)length)..]);
            if (Crc32(png.Slice(pos + 4, 4 + (int)length)) != crc) throw new PngFormatException($"the {Ascii(type)} chunk's CRC is wrong");
            pos += 12 + (int)length;

            var name = Ascii(type);
            switch (name)
            {
                case "IHDR":
                    if (seenHeader || data.Length != 13) throw new PngFormatException("a bad IHDR");
                    seenHeader = true;
                    width = (int)BinaryPrimitives.ReadUInt32BigEndian(data);
                    height = (int)BinaryPrimitives.ReadUInt32BigEndian(data[4..]);
                    var depth = data[8];
                    var colour = data[9];
                    var interlace = data[12];
                    if (width <= 0 || height <= 0 || width > MaxSide || height > MaxSide || (long)width * height > MaxPixels)
                    {
                        throw new PngFormatException($"{width}x{height}: not a generated picture's size (at most {MaxSide} a side, {MaxPixels} pixels)");
                    }
                    if (depth != 8) throw new PngFormatException($"{depth}-bit samples: only 8-bit is read");
                    channels = colour switch
                    {
                        2 => 3,
                        6 => 4,
                        0 => throw new PngFormatException("greyscale: only RGB and RGBA are read"),
                        3 => throw new PngFormatException("palette: only RGB and RGBA are read"),
                        4 => throw new PngFormatException("grey with alpha: only RGB and RGBA are read"),
                        _ => throw new PngFormatException($"colour type {colour}: only RGB and RGBA are read"),
                    };
                    if (interlace != 0) throw new PngFormatException("interlaced: only non-interlaced is read");
                    break;
                case "PLTE":
                    throw new PngFormatException("a palette chunk in an RGB picture");
                case "tRNS":
                    if (!seenHeader) throw new PngFormatException("tRNS before IHDR");
                    if (channels == 3)
                    {
                        if (data.Length != 6) throw new PngFormatException("a bad tRNS");
                        // 16-bit samples of an 8-bit image: the low byte is the value.
                        colourKey = (data[1], data[3], data[5]);
                    }
                    break;
                case "IDAT":
                    if (!seenHeader) throw new PngFormatException("IDAT before IHDR");
                    idat.Write(data);
                    break;
                case "IEND":
                    seenEnd = true;
                    break;
                default:
                    // An unknown chunk whose first letter is upper case is critical: not ours.
                    if ((type[0] & 0x20) == 0) throw new PngFormatException($"an unknown critical chunk: {name}");
                    break;
            }
        }
        if (!seenHeader) throw new PngFormatException("no IHDR");
        if (idat.Length == 0) throw new PngFormatException("no image data");

        // Every scanline is a filter byte plus the samples: the inflated length is known
        // exactly, and a stream that gives more or less is not this picture.
        var stride = 1 + width * channels;
        var expected = (long)stride * height;
        var raw = new byte[expected];
        idat.Position = 0;
        // Bad compressed data is the picture's fault, in the picture's words: the inflater's
        // own exception must not escape as if the codec had crashed.
        try
        {
            using var inflate = new ZLibStream(idat, CompressionMode.Decompress);
            var got = 0;
            while (got < raw.Length)
            {
                var n = inflate.Read(raw, got, raw.Length - got);
                if (n == 0) throw new PngFormatException($"the image data ends early: {got} of {expected} bytes");
                got += n;
            }
            if (inflate.ReadByte() != -1) throw new PngFormatException("the image data is longer than the picture");
        }
        catch (InvalidDataException ex)
        {
            throw new PngFormatException("the image data is not valid zlib: " + ex.Message);
        }

        var img = new RgbaImage(width, height);
        var prev = new byte[stride - 1];
        var line = new byte[stride - 1];
        for (var y = 0; y < height; y++)
        {
            var filter = raw[y * stride];
            Array.Copy(raw, y * stride + 1, line, 0, stride - 1);
            Unfilter(filter, line, prev, channels);
            for (var x = 0; x < width; x++)
            {
                var s = x * channels;
                var a = channels == 4 ? line[s + 3]
                    : colourKey is { } key && line[s] == key.R && line[s + 1] == key.G && line[s + 2] == key.B ? (byte)0
                    : (byte)255;
                img[x, y] = (line[s], line[s + 1], line[s + 2], a);
            }
            (prev, line) = (line, prev);
        }
        return img;
    }

    /// <summary>An RGBA PNG, one IDAT, filter 0 on every line: what the image tool is handed.</summary>
    public static byte[] Write(RgbaImage img)
    {
        var raw = new byte[(1 + img.Width * 4) * img.Height];
        for (var y = 0; y < img.Height; y++)
        {
            Buffer.BlockCopy(img.Pixels, img.Offset(0, y), raw, y * (1 + img.Width * 4) + 1, img.Width * 4);
        }
        var zipped = new MemoryStream();
        using (var deflate = new ZLibStream(zipped, CompressionLevel.Optimal, leaveOpen: true)) deflate.Write(raw);

        var header = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)img.Width);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), (uint)img.Height);
        header[8] = 8;   // bit depth
        header[9] = 6;   // RGBA
        var output = new MemoryStream();
        output.Write(Signature);
        Chunk(output, "IHDR", header);
        Chunk(output, "IDAT", zipped.ToArray());
        Chunk(output, "IEND", []);
        return output.ToArray();
    }

    public static void Write(string path, RgbaImage img) => File.WriteAllBytes(path, Write(img));

    /// <summary>One chunk: length, type, data, CRC over type and data. Public for the tests' own vectors.</summary>
    public static void Chunk(Stream to, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> len = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(len, (uint)data.Length);
        to.Write(len);
        var typeAndData = new byte[4 + data.Length];
        for (var i = 0; i < 4; i++) typeAndData[i] = (byte)type[i];
        data.CopyTo(typeAndData.AsSpan(4));
        to.Write(typeAndData);
        Span<byte> crc = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crc, Crc32(typeAndData));
        to.Write(crc);
    }

    private static void Unfilter(byte filter, byte[] line, byte[] prev, int bpp)
    {
        switch (filter)
        {
            case 0:
                break;
            case 1:
                for (var i = bpp; i < line.Length; i++) line[i] = (byte)(line[i] + line[i - bpp]);
                break;
            case 2:
                for (var i = 0; i < line.Length; i++) line[i] = (byte)(line[i] + prev[i]);
                break;
            case 3:
                for (var i = 0; i < line.Length; i++)
                {
                    var left = i >= bpp ? line[i - bpp] : 0;
                    line[i] = (byte)(line[i] + ((left + prev[i]) >> 1));
                }
                break;
            case 4:
                for (var i = 0; i < line.Length; i++)
                {
                    int a = i >= bpp ? line[i - bpp] : 0, b = prev[i], c = i >= bpp ? prev[i - bpp] : 0;
                    var p = a + b - c;
                    int pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
                    var pred = pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
                    line[i] = (byte)(line[i] + pred);
                }
                break;
            default:
                throw new PngFormatException($"filter type {filter} is not a PNG filter");
        }
    }

    private static string Ascii(ReadOnlySpan<byte> four) => new([(char)four[0], (char)four[1], (char)four[2], (char)four[3]]);

    private static readonly uint[] CrcTable = MakeCrcTable();

    private static uint[] MakeCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        return table;
    }

    public static uint Crc32(ReadOnlySpan<byte> data)
    {
        var c = 0xFFFFFFFFu;
        foreach (var b in data) c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFFu;
    }
}
