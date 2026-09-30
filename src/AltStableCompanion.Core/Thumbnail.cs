namespace AltStableCompanion.Core;

/// <summary>A thumbnail that cannot be made from what the manifest says: a crop the canvas does not hold.</summary>
public sealed class ThumbnailException(string message) : Exception(message);

/// <summary>
/// The small picture of a portrait the window's list shows: the cutout as the game draws it,
/// cropped by the manifest's numbers - not by what the pixels look like - and scaled down.
/// </summary>
public static class Thumbnail
{
    /// <summary>
    /// The top-left <paramref name="w"/> × <paramref name="h"/> of the canvas, at most
    /// <paramref name="height"/> tall (a shorter crop is not scaled up). A crop the canvas
    /// cannot hold is a manifest that lies, and is refused rather than read across rows.
    /// </summary>
    public static RgbaImage Make(RgbaImage canvas, int w, int h, int height)
    {
        if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height), "a thumbnail has a height");
        if (w <= 0 || h <= 0 || w > canvas.Width || h > canvas.Height)
        {
            throw new ThumbnailException($"a {w}x{h} crop of a {canvas.Width}x{canvas.Height} canvas");
        }
        var crop = canvas.Crop(0, 0, w, h);
        // The resampler works in doubles over the whole source. A crop far taller than the
        // thumbnail is first shrunk by whole blocks, cheaply, to no less than twice the target:
        // the resampler then has little to do, and nothing to show for the difference at 48 px.
        var k = h / (2 * height);
        if (k >= 2) crop = BoxShrink(crop, k);
        return Resampler.DownscaleToHeight(crop, height);
    }

    /// <summary>
    /// Every <paramref name="k"/> × <paramref name="k"/> block averaged into one pixel, colours
    /// weighted by alpha so transparent padding does not darken an edge. Rows and columns that
    /// do not fill a block are dropped.
    /// </summary>
    public static RgbaImage BoxShrink(RgbaImage src, int k)
    {
        if (k < 1) throw new ArgumentOutOfRangeException(nameof(k));
        var dst = new RgbaImage(Math.Max(1, src.Width / k), Math.Max(1, src.Height / k));
        for (var y = 0; y < dst.Height; y++)
        {
            for (var x = 0; x < dst.Width; x++)
            {
                long r = 0, g = 0, b = 0, a = 0;
                for (var yy = 0; yy < k; yy++)
                {
                    var sy = Math.Min(y * k + yy, src.Height - 1);
                    for (var xx = 0; xx < k; xx++)
                    {
                        var i = src.Offset(Math.Min(x * k + xx, src.Width - 1), sy);
                        var alpha = src.Pixels[i + 3];
                        r += src.Pixels[i] * alpha;
                        g += src.Pixels[i + 1] * alpha;
                        b += src.Pixels[i + 2] * alpha;
                        a += alpha;
                    }
                }
                var n = k * k;
                dst[x, y] = a == 0
                    ? ((byte)0, (byte)0, (byte)0, (byte)0)
                    : ((byte)((r + a / 2) / a), (byte)((g + a / 2) / a), (byte)((b + a / 2) / a), (byte)((a + n / 2) / n));
            }
        }
        return dst;
    }
}

/// <summary>
/// The pictures for the rows, each made once and kept while its row is unchanged: same
/// cutouts folder, same file, same modified time, same crop. A file that could not be read
/// this time is not remembered - the next look tries again - and nothing is ever written.
/// What a picture IS (a bitmap the window can draw) is the caller's, through
/// <paramref name="make"/>; so is letting it go, through <paramref name="release"/>, which is
/// called for every picture no row uses any more, and for all of them on dispose.
/// </summary>
public sealed class ThumbnailCache<TImage>(int height, Func<RgbaImage, TImage> make, Action<TImage> release) : IDisposable
    where TImage : class
{
    private readonly record struct Key(string Dir, string File, DateTime Modified, int W, int H);

    private readonly Dictionary<Key, TImage> _kept = [];
    private readonly HashSet<Key> _used = [];

    /// <summary>
    /// The row's picture, or null: a row with no file has none (<paramref name="failed"/>
    /// false), and one whose file could not be read or cropped has none this time
    /// (<paramref name="failed"/> true). Anything else that goes wrong is a bug, and shows.
    /// </summary>
    public TImage? Get(string cutoutsDir, PortraitRow row, out bool failed)
    {
        failed = false;
        if (row.FileName is null || row.Size is not { } size || row.FileModified is not { } modified) return null;
        var key = new Key(cutoutsDir, row.FileName, modified, size.W, size.H);
        _used.Add(key);
        if (_kept.TryGetValue(key, out var image)) return image;
        try
        {
            image = make(Thumbnail.Make(TgaCodec.Read(Path.Combine(cutoutsDir, row.FileName)), size.W, size.H, height));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TgaFormatException or ThumbnailException)
        {
            failed = true;
            return null;
        }
        _kept[key] = image;
        return image;
    }

    /// <summary>
    /// Let go of every picture no <see cref="Get"/> asked for since the last sweep. Called
    /// after the rows that show the pictures have been replaced, never before.
    /// </summary>
    public void Sweep()
    {
        foreach (var (key, image) in _kept.Where(k => !_used.Contains(k.Key)).ToList())
        {
            _kept.Remove(key);
            release(image);
        }
        _used.Clear();
    }

    public void Dispose()
    {
        foreach (var image in _kept.Values) release(image);
        _kept.Clear();
        _used.Clear();
    }
}
