namespace AltStableCompanion.Core;

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
        if (w <= 0 || h <= 0 || w > canvas.Width || h > canvas.Height)
        {
            throw new ArgumentOutOfRangeException(nameof(w), $"a {w}x{h} crop of a {canvas.Width}x{canvas.Height} canvas");
        }
        if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height), "a thumbnail has a height");
        return Resampler.DownscaleToHeight(canvas.Crop(0, 0, w, h), height);
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
    /// (<paramref name="failed"/> true).
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
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TgaFormatException or ArgumentOutOfRangeException)
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
