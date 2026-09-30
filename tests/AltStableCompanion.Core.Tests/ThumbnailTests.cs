using AltStableCompanion.Core;

namespace AltStableCompanion.Core.Tests;

public class ThumbnailTests
{
    private static readonly DateTime T0 = new(2026, 9, 29, 12, 0, 0);

    // A canvas whose every pixel says where it is: red = x, green = y.
    private static RgbaImage Canvas(int w, int h)
    {
        var img = new RgbaImage(w, h);
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
                img[x, y] = ((byte)x, (byte)y, 0, 255);
        return img;
    }

    [Fact]
    public void The_thumbnail_is_the_top_left_crop_the_manifest_names_scaled_down_and_never_up()
    {
        var canvas = Canvas(16, 32);
        var crop = Thumbnail.Make(canvas, 6, 10, 100);
        Assert.Equal((6, 10), (crop.Width, crop.Height));
        Assert.Equal(((byte)5, (byte)9, (byte)0, (byte)255), crop[5, 9]);
        Assert.Equal(((byte)0, (byte)0, (byte)0, (byte)255), crop[0, 0]);

        var small = Thumbnail.Make(canvas, 8, 32, 8);
        Assert.Equal((2, 8), (small.Width, small.Height));

        // A manifest that lies is refused, not read across rows - and it is the manifest's
        // fault, not a bug's: its own exception, so a bug is never taken for it.
        Assert.Throws<ThumbnailException>(() => Thumbnail.Make(canvas, 17, 10, 8));
        Assert.Throws<ThumbnailException>(() => Thumbnail.Make(canvas, 6, 33, 8));
        Assert.Throws<ThumbnailException>(() => Thumbnail.Make(canvas, 0, 10, 8));
        Assert.Throws<ArgumentOutOfRangeException>(() => Thumbnail.Make(canvas, 6, 10, 0));
    }

    [Fact]
    public void A_tall_crop_is_first_shrunk_by_blocks_weighted_by_alpha()
    {
        // A 4x4 block: three opaque reds and one transparent pixel. The colour is the reds'
        // alone; the alpha is a quarter gone.
        var src = new RgbaImage(4, 4);
        for (var y = 0; y < 4; y++)
            for (var x = 0; x < 4; x++)
                src[x, y] = (200, 0, 0, 255);
        src[1, 1] = (0, 0, 255, 0);
        for (var x = 0; x < 4; x++) src[x, 3] = (0, 0, 255, 0);
        var one = Thumbnail.BoxShrink(src, 4);
        Assert.Equal((1, 1), (one.Width, one.Height));
        Assert.Equal(((byte)200, (byte)0, (byte)0, (byte)175), one[0, 0]);

        // Blocks of 2: four pixels, and the row that does not fill a block is dropped.
        var two = Thumbnail.BoxShrink(new RgbaImage(5, 5), 2);
        Assert.Equal((2, 2), (two.Width, two.Height));

        // Make shrinks a crop taller than four times the thumbnail by blocks first; the
        // result is the same size either way, and a solid colour stays that colour.
        var tall = new RgbaImage(12, 96);
        for (var y = 0; y < 96; y++)
            for (var x = 0; x < 12; x++)
                tall[x, y] = (10, 20, 30, 255);
        var thumb = Thumbnail.Make(tall, 12, 96, 8);
        Assert.Equal((1, 8), (thumb.Width, thumb.Height));
        Assert.Equal(((byte)10, (byte)20, (byte)30, (byte)255), thumb[0, 4]);
    }

    private static PortraitRow Row(string file, DateTime modified, int w = 4, int h = 8) =>
        new("Aaa", "g1", file, PortraitSource.ByGuid, modified, T0, CaptureOutcome.Converted, null, Size: (w, h));

    private sealed class Made
    {
        public readonly List<RgbaImage?> Released = [];
        public ThumbnailCache<RgbaImage> Cache;
        public Made() => Cache = new(8, img => img, img => Released.Add(img!));
    }

    [Fact]
    public void A_picture_is_made_once_and_kept_while_its_row_is_unchanged()
    {
        using var t = new TempInstall();
        var dir = Path.Combine(t.Root, "cutouts");
        Directory.CreateDirectory(dir);
        TgaCodec.Write(Path.Combine(dir, "aaa.tga"), Canvas(8, 16));
        File.SetLastWriteTime(Path.Combine(dir, "aaa.tga"), T0);
        var m = new Made();

        var first = m.Cache.Get(dir, Row("aaa.tga", T0), out var failed);
        Assert.NotNull(first);
        Assert.False(failed);
        Assert.Equal((4, 8), (first.Width, first.Height));
        Assert.Same(first, m.Cache.Get(dir, Row("aaa.tga", T0), out _));
        m.Cache.Sweep();
        Assert.Empty(m.Released);
        Assert.Same(first, m.Cache.Get(dir, Row("aaa.tga", T0), out _));
        m.Cache.Sweep();

        // A round is every row's Get, then a sweep. The file rewritten: a new picture, and
        // the old one - which no row of this round asked for - let go once swept.
        var rewritten = m.Cache.Get(dir, Row("aaa.tga", T0.AddMinutes(1)), out _);
        Assert.NotSame(first, rewritten);
        m.Cache.Sweep();
        Assert.Equal([first], m.Released);

        // Another crop of the same file, and the same file in another folder: other pictures.
        var otherCrop = m.Cache.Get(dir, Row("aaa.tga", T0.AddMinutes(1), 2, 4), out _);
        Assert.NotSame(rewritten, otherCrop);
        var other = Path.Combine(t.Root, "other");
        Directory.CreateDirectory(other);
        TgaCodec.Write(Path.Combine(other, "aaa.tga"), Canvas(8, 16));
        var elsewhere = m.Cache.Get(other, Row("aaa.tga", T0.AddMinutes(1)), out _);
        Assert.NotSame(rewritten, elsewhere);
        m.Cache.Sweep();
        Assert.Equal([first, rewritten], m.Released.Select(i => i!));

        // A row with no file has no picture, and that is not a failure.
        Assert.Null(m.Cache.Get(dir, Row("aaa.tga", T0) with { FileName = null }, out failed));
        Assert.False(failed);
        Assert.Null(m.Cache.Get(dir, Row("aaa.tga", T0) with { Size = null }, out failed));
        Assert.False(failed);

        m.Cache.Dispose();
        Assert.Equal(4, m.Released.Count);
    }

    [Fact]
    public void A_file_that_could_not_be_read_is_tried_again_next_time_not_remembered()
    {
        using var t = new TempInstall();
        var dir = Path.Combine(t.Root, "cutouts");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "aaa.tga");
        var m = new Made();

        // Missing.
        Assert.Null(m.Cache.Get(dir, Row("aaa.tga", T0), out var failed));
        Assert.True(failed);

        // Cut short - and then whole again, with the same modified time.
        var whole = new MemoryStream();
        TgaCodec.Write(whole, Canvas(8, 16));
        File.WriteAllBytes(path, whole.ToArray()[..^100]);
        File.SetLastWriteTime(path, T0);
        Assert.Null(m.Cache.Get(dir, Row("aaa.tga", T0), out failed));
        Assert.True(failed);
        File.WriteAllBytes(path, whole.ToArray());
        File.SetLastWriteTime(path, T0);
        Assert.NotNull(m.Cache.Get(dir, Row("aaa.tga", T0), out failed));
        Assert.False(failed);

        // A manifest that lies about the crop: no picture, no exception out of the cache.
        Assert.Null(m.Cache.Get(dir, Row("aaa.tga", T0, 9, 8), out failed));
        Assert.True(failed);

        // In use.
        using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.Null(m.Cache.Get(dir, Row("aaa.tga", T0.AddMinutes(1)), out failed));
            Assert.True(failed);
        }
        Assert.NotNull(m.Cache.Get(dir, Row("aaa.tga", T0.AddMinutes(1)), out failed));
        Assert.False(failed);
        Assert.Empty(m.Released);

        // A bug is not "the file could not be read": it shows.
        var buggy = new ThumbnailCache<RgbaImage>(8, _ => throw new ArgumentOutOfRangeException("bug"), _ => { });
        Assert.Throws<ArgumentOutOfRangeException>(() => buggy.Get(dir, Row("aaa.tga", T0.AddMinutes(1)), out _));
    }
}
