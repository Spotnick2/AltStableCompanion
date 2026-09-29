namespace AltStableCompanion.Core;

/// <summary>The two shots are not a black/white pair of one pose. The capture can be retried.</summary>
public sealed class NotAPairException(string message) : Exception(message);

/// <summary>
/// Exact alpha from the same frozen pose shot on black and on white:
///
///     alpha  = 1 - ((w.r-b.r) + (w.g-b.g) + (w.b-b.b)) / 765
///     colour = black / alpha
///
/// An opaque pixel reads the same on both backdrops; a transparent one differs by the full
/// 255. Ported from make-cutout.py's numpy path (matte_numpy), which is the one that ran:
/// colour TRUNCATES after the divide, alpha rounds, and anything at or below the floor is
/// fully transparent.
/// </summary>
public static class Matte
{
    /// <summary>Below this coverage a pixel is backdrop, not a faint edge.</summary>
    public const double AlphaFloor = 0.02;

    /// <summary>More of the frame than this reading opaque means the shots were identical.</summary>
    public const double IdenticalCoverage = 0.9;

    /// <summary>The figure with its recovered alpha, cropped to its bounds.</summary>
    public static RgbaImage Compute(RgbaImage black, RgbaImage white)
    {
        if (black.Width != white.Width || black.Height != white.Height)
        {
            throw new NotAPairException(
                $"the two shots differ in size ({black.Width}x{black.Height} vs {white.Width}x{white.Height})");
        }

        var n = black.Width * black.Height;
        var outImg = new RgbaImage(black.Width, black.Height);
        var b = black.Pixels;
        var w = white.Pixels;
        var o = outImg.Pixels;
        var covered = 0;

        for (var p = 0; p < n; p++)
        {
            var i = p * 4;
            var diff = ((w[i] - b[i]) + (w[i + 1] - b[i + 1]) + (w[i + 2] - b[i + 2])) / (3.0 * 255.0);
            var alpha = Math.Clamp(1.0 - diff, 0.0, 1.0);
            if (alpha > 0.5) covered++;
            if (alpha <= AlphaFloor) continue;           // stays 0,0,0,0

            // The black shot is already premultiplied by coverage; undo that.
            o[i] = (byte)Math.Clamp(b[i] / alpha, 0, 255);
            o[i + 1] = (byte)Math.Clamp(b[i + 1] / alpha, 0, 255);
            o[i + 2] = (byte)Math.Clamp(b[i + 2] / alpha, 0, 255);
            o[i + 3] = (byte)(alpha * 255 + 0.5);
        }

        // A real capture is a figure on an empty stage, so near-total coverage means the pair
        // was wrong - the backdrop did not change between them - not a figure filling the screen.
        if ((double)covered / n > IdenticalCoverage)
        {
            throw new NotAPairException(
                $"the two shots look identical ({100.0 * covered / n:0}% of the frame reads as opaque)");
        }

        var box = outImg.AlphaBounds()
            ?? throw new NotAPairException("nothing but backdrop in those two shots - was the stage showing?");
        return outImg.Crop(box.X, box.Y, box.Width, box.Height);
    }
}
