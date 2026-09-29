using AltStableCompanion.Core;

namespace AltStableCompanion.Core.Tests;

public class TgaCodecTests
{
    private static RgbaImage Gradient(int w, int h)
    {
        var img = new RgbaImage(w, h);
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
                img[x, y] = ((byte)(x * 7), (byte)(y * 11), (byte)((x + y) * 3), (byte)(x * 13 + y));
        return img;
    }

    [Fact]
    public void A_wow_screenshot_decodes_with_packets_crossing_rows()
    {
        // Two solid bands whose boundary is mid-row: every run straddles a row edge.
        var img = new RgbaImage(5, 4);
        for (var i = 0; i < 20; i++)
        {
            var c = i < 7 ? (byte)40 : (byte)200;
            img.Pixels[i * 4] = c; img.Pixels[i * 4 + 1] = 90; img.Pixels[i * 4 + 2] = 10; img.Pixels[i * 4 + 3] = 255;
        }
        var bytes = TestData.WowScreenshot(img);
        Assert.Equal(10, bytes[2]);                              // it really is RLE
        var back = TgaCodec.Read(new MemoryStream(bytes));
        Assert.Equal(img.Pixels, back.Pixels);
    }

    [Fact]
    public void A_screenshots_padding_byte_is_not_read_as_alpha()
    {
        // WoW writes 32 bpp with 0 alpha bits and a zero fourth byte: that is padding.
        var back = TgaCodec.Read(new MemoryStream(TestData.WowScreenshot(TestData.Solid(3, 3, 1, 2, 3))));
        Assert.All(Enumerable.Range(0, 9), i => Assert.Equal(255, back.Pixels[i * 4 + 3]));
    }

    [Fact]
    public void A_written_cutout_round_trips_with_its_alpha()
    {
        var img = Gradient(9, 6);
        using var ms = new MemoryStream();
        TgaCodec.Write(ms, img);
        var bytes = ms.ToArray();
        Assert.Equal(2, bytes[2]);                               // uncompressed
        Assert.Equal(32, bytes[16]);
        Assert.Equal(0x08, bytes[17]);                           // bottom-left, 8 alpha bits: the known-good layout
        var back = TgaCodec.Read(new MemoryStream(bytes));
        Assert.Equal(img.Pixels, back.Pixels);
    }

    [Fact]
    public void Bottom_left_files_store_their_bottom_row_first()
    {
        var img = TestData.Solid(2, 2, 0, 0, 0);
        img[0, 1] = (9, 8, 7, 6);                                // bottom-left pixel
        using var ms = new MemoryStream();
        TgaCodec.Write(ms, img);
        var bytes = ms.ToArray();
        Assert.Equal(new byte[] { 7, 8, 9, 6 }, bytes[18..22]); // first pixel stored is (0, bottom), BGRA
    }

    [Fact]
    public void A_24_bit_file_reads_as_opaque()
    {
        var header = new byte[18];
        header[2] = 2; header[12] = 1; header[14] = 1; header[16] = 24; header[17] = 0x20;
        var back = TgaCodec.Read(new MemoryStream([.. header, 30, 20, 10]));
        Assert.Equal(((byte)10, (byte)20, (byte)30, (byte)255), back[0, 0]);
    }

    [Fact]
    public void A_file_still_being_written_is_a_format_error()
    {
        var bytes = TestData.WowScreenshot(Gradient(8, 8));
        Assert.Throws<TgaFormatException>(() => TgaCodec.Read(new MemoryStream(bytes[..(bytes.Length / 2)])));
    }

    [Fact]
    public void A_run_past_the_end_of_the_image_is_refused()
    {
        var header = new byte[18];
        header[2] = 10; header[12] = 2; header[14] = 1; header[16] = 32; header[17] = 0x20;
        // One RLE packet of 5 pixels in a 2-pixel image.
        Assert.Throws<TgaFormatException>(() =>
            TgaCodec.Read(new MemoryStream([.. header, 0x84, 1, 2, 3, 0])));
    }
}

public class MatteTests
{
    [Fact]
    public void An_opaque_figure_comes_back_opaque_and_cropped_to_itself()
    {
        var (b, w) = TestData.Pair(64, 64, 20, 10, 10, 30);
        var cut = Matte.Compute(b, w);
        Assert.Equal((10, 30), (cut.Width, cut.Height));
        Assert.Equal(((byte)255, (byte)0, (byte)0, (byte)255), cut[5, 5]);
    }

    [Fact]
    public void Half_coverage_recovers_half_alpha_and_the_true_colour()
    {
        var (b, w) = TestData.Pair(64, 64, 20, 10, 10, 30, halfColumn: true);
        var cut = Matte.Compute(b, w);
        var edge = cut[10, 5];                                   // the column right of the figure
        // alpha = 1 - (127+128+128)/765 = 0.4993..., colour = 128/alpha, truncated as numpy does.
        Assert.Equal(127, edge.A);
        Assert.Equal(255, edge.R);
        Assert.Equal(0, edge.G);
    }

    [Fact]
    public void Identical_shots_are_not_a_pair()
    {
        var img = TestData.Solid(40, 40, 90, 90, 90);
        var ex = Assert.Throws<NotAPairException>(() => Matte.Compute(img, img));
        Assert.Contains("identical", ex.Message);
    }

    [Fact]
    public void Nothing_but_backdrop_is_not_a_pair()
    {
        var ex = Assert.Throws<NotAPairException>(() =>
            Matte.Compute(TestData.Solid(8, 8, 0, 0, 0), TestData.Solid(8, 8, 255, 255, 255)));
        Assert.Contains("backdrop", ex.Message);
    }

    [Fact]
    public void Shots_of_different_sizes_are_not_a_pair() =>
        Assert.Throws<NotAPairException>(() =>
            Matte.Compute(TestData.Solid(8, 8, 0, 0, 0), TestData.Solid(9, 8, 255, 255, 255)));
}

public class ResamplerTests
{
    [Fact]
    public void An_image_no_taller_than_the_target_is_left_alone()
    {
        var img = TestData.Solid(10, 100, 1, 2, 3);
        Assert.Same(img, Resampler.DownscaleToHeight(img, 512));
    }

    [Fact]
    public void Downscaling_keeps_the_aspect_and_hits_the_height()
    {
        var out1 = Resampler.DownscaleToHeight(TestData.Solid(300, 1200, 9, 9, 9), 512);
        Assert.Equal(512, out1.Height);
        Assert.Equal(128, out1.Width);                           // 300 * 512 / 1200
    }

    [Fact]
    public void Edges_get_soft_alpha_without_a_dark_halo()
    {
        // An opaque red figure on transparency: the classic failure is resampling straight
        // alpha, which averages edge colour with transparent black and darkens it.
        var img = new RgbaImage(40, 200);
        for (var y = 50; y < 150; y++)
            for (var x = 13; x < 27; x++)
                img[x, y] = (255, 0, 0, 255);
        var small = Resampler.DownscaleToHeight(img, 50);
        var partial = 0;
        for (var y = 0; y < small.Height; y++)
        {
            for (var x = 0; x < small.Width; x++)
            {
                var p = small[x, y];
                if (p.A == 0)
                {
                    Assert.Equal((0, 0, 0), (p.R, p.G, p.B));    // clear means clear
                    continue;
                }
                if (p.A < 255) partial++;
                Assert.Equal(255, p.R);                          // still red, not darkened
                Assert.True(p.G <= 1 && p.B <= 1, $"({p.R},{p.G},{p.B}) at {x},{y}");
            }
        }
        Assert.True(partial > 0, "no soft edge was produced");
    }

    [Fact]
    public void Colour_is_weighted_by_coverage_so_a_nearly_clear_neighbour_cannot_tint_the_edge()
    {
        // Opaque red beside nearly-clear BLUE. Weighted by coverage, the blue contributes
        // almost nothing and the edge stays red; averaged straight, it turns the edge magenta.
        // (Opaque-on-fully-clear cannot tell the two apart: there the colours coincide.)
        var img = new RgbaImage(40, 200);
        for (var y = 0; y < 200; y++)
        {
            for (var x = 0; x < 40; x++)
            {
                img[x, y] = x < 20 ? ((byte)255, (byte)0, (byte)0, (byte)255) : ((byte)0, (byte)0, (byte)255, (byte)8);
            }
        }
        var small = Resampler.DownscaleToHeight(img, 50);
        // The boundary pixels: mostly covered, so mostly the opaque red. Straight averaging
        // weighs the blue by COUNT (about half) instead of by coverage (almost nothing).
        // Measured on this image: the alpha-239 boundary pixel is (254,0,1) weighted by coverage
        // and (254,0,18) averaged straight; the alpha-24 one is (175,0,80) against (175,0,255).
        var row = Enumerable.Range(0, small.Width).Select(x => small[x, 25]).ToList();
        var mostlyCovered = row.Where(p => p.A is > 128 and < 255).ToList();
        Assert.NotEmpty(mostlyCovered);
        Assert.All(mostlyCovered, e => Assert.True(e.B <= 5, $"mostly-red edge tinted blue: ({e.R},{e.G},{e.B},{e.A})"));
        // Where the red's coverage outweighs the blue's, red stays the dominant channel.
        var mixed = row.Where(p => p.A is >= 16 and < 255).ToList();
        Assert.All(mixed, e => Assert.True(e.R > e.B, $"blue outweighs red: ({e.R},{e.G},{e.B},{e.A})"));
    }
}

public class CutoutConverterTests
{
    [Fact]
    public void A_figure_is_scaled_to_512_on_a_power_of_two_canvas_with_screen_fractions()
    {
        var (b, w) = TestData.Pair(400, 1200, 150, 250, 100, 700);
        var cut = CutoutConverter.Convert(b, w, "Player-1-AAAA", 1790000000);
        Assert.Equal((73, 512), (cut.Meta.W, cut.Meta.H));      // round(100*512/700) = 73
        Assert.Equal((128, 512), (cut.Meta.TexW, cut.Meta.TexH));
        Assert.Equal((128, 512), (cut.Canvas.Width, cut.Canvas.Height));
        Assert.Equal(0.08333, cut.Meta.NativeW);
        Assert.Equal(0.58333, cut.Meta.NativeH);
        Assert.Equal("screen", cut.Meta.NativeUnit);
        Assert.Equal([100, 700], cut.Meta.NativePx!);
        Assert.Equal("Player-1-AAAA", cut.Meta.Guid);
        Assert.Equal(1790000000, cut.Meta.Epoch);
        Assert.False(cut.NearlySquare);
        Assert.Equal(0, cut.Canvas[100, 10].A);                  // right of the content: padding
    }

    [Fact]
    public void A_figure_as_tall_as_the_screen_is_the_whole_window()
    {
        var (b, w) = TestData.Pair(400, 200, 100, 2, 50, 196);
        Assert.Throws<NotAPairException>(() => CutoutConverter.Convert(b, w));
    }

    [Fact]
    public void A_nearly_square_cutout_is_flagged()
    {
        var (b, w) = TestData.Pair(400, 1200, 100, 300, 90, 100);
        Assert.True(CutoutConverter.Convert(b, w).NearlySquare);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(73, 128)]
    [InlineData(128, 128)]
    [InlineData(129, 256)]
    [InlineData(512, 512)]
    public void Pot_is_the_next_power_of_two(int n, int want) => Assert.Equal(want, CutoutConverter.Pot(n));
}
