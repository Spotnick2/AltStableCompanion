namespace AltStableCompanion.Core;

/// <summary>
/// Premultiplied Lanczos-3 downscaling - what manufactures the soft edges the client will
/// not give us.
///
/// The client renders with NO partial alpha (measured on 1.60.1.70009): every pixel is fully
/// opaque or fully clear, so a raw cutout has hard, aliased edges. The capture is far larger
/// than the scene draws it, so resampling it down creates the intermediate coverage. Colour is
/// multiplied by coverage FIRST, or every edge pixel averages with the transparent black around
/// it and leaves a dark halo.
///
/// Same kernel and windowing as Pillow's LANCZOS (the Python converter's), but with float
/// intermediates through both passes: Lanczos has negative lobes, and quantising between the
/// passes is where Pillow's rounding comes from. So results agree to within a shade, not
/// byte for byte.
/// </summary>
public static class Resampler
{
    private const double Support = 3.0;

    /// <summary>Scaled to <paramref name="targetHeight"/>, keeping the aspect; unchanged when not taller.</summary>
    public static RgbaImage DownscaleToHeight(RgbaImage src, int targetHeight)
    {
        if (targetHeight <= 0 || src.Height <= targetHeight) return src;

        // round() in Python rounds halves to even; so does this.
        var newWidth = Math.Max(1, (int)Math.Round(src.Width * targetHeight / (double)src.Height,
            MidpointRounding.ToEven));

        // Premultiply into floats: 0..255 colour scaled by coverage, and the coverage itself.
        var w = src.Width;
        var h = src.Height;
        var plane = new double[w * h * 4];
        for (var p = 0; p < w * h; p++)
        {
            var i = p * 4;
            var a = src.Pixels[i + 3];
            plane[i] = src.Pixels[i] * a / 255.0;
            plane[i + 1] = src.Pixels[i + 1] * a / 255.0;
            plane[i + 2] = src.Pixels[i + 2] * a / 255.0;
            plane[i + 3] = a;
        }

        // Horizontal first, then vertical, as Pillow does.
        var horizontal = Pass(plane, w, h, newWidth, horizontalPass: true);
        var both = Pass(horizontal, newWidth, h, targetHeight, horizontalPass: false);

        var dst = new RgbaImage(newWidth, targetHeight);
        for (var p = 0; p < newWidth * targetHeight; p++)
        {
            var i = p * 4;
            var a = Math.Clamp(both[i + 3], 0, 255);
            var aByte = (byte)Math.Round(a, MidpointRounding.AwayFromZero);
            if (aByte == 0) continue;                    // clear means clear: no colour left behind
            for (var c = 0; c < 3; c++)
            {
                // Clamp premultiplied colour to the coverage before dividing it back out; the
                // negative lobes can push it either side.
                var pc = Math.Clamp(both[i + c], 0, a);
                dst.Pixels[i + c] = (byte)Math.Min(255, Math.Round(pc * 255.0 / a, MidpointRounding.AwayFromZero));
            }
            dst.Pixels[i + 3] = aByte;
        }
        return dst;
    }

    private static double[] Pass(double[] src, int srcW, int srcH, int outSize, bool horizontalPass)
    {
        var inSize = horizontalPass ? srcW : srcH;
        var (bounds, weights, span) = Coefficients(inSize, outSize);
        var dstW = horizontalPass ? outSize : srcW;
        var dstH = horizontalPass ? srcH : outSize;
        var dst = new double[dstW * dstH * 4];

        for (var y = 0; y < dstH; y++)
        {
            for (var x = 0; x < dstW; x++)
            {
                var o = horizontalPass ? x : y;
                var (start, len) = bounds[o];
                double r = 0, g = 0, b = 0, a = 0;
                for (var k = 0; k < len; k++)
                {
                    var wgt = weights[o * span + k];
                    var si = horizontalPass
                        ? (y * srcW + start + k) * 4
                        : ((start + k) * srcW + x) * 4;
                    r += src[si] * wgt;
                    g += src[si + 1] * wgt;
                    b += src[si + 2] * wgt;
                    a += src[si + 3] * wgt;
                }
                var di = (y * dstW + x) * 4;
                dst[di] = r;
                dst[di + 1] = g;
                dst[di + 2] = b;
                dst[di + 3] = a;
            }
        }
        return dst;
    }

    // Pillow's precompute_coeffs: the window of source pixels each output pixel reads, and
    // their normalised weights.
    private static ((int Start, int Len)[] Bounds, double[] Weights, int Span) Coefficients(int inSize, int outSize)
    {
        var scale = inSize / (double)outSize;
        var filterScale = Math.Max(1.0, scale);
        var support = Support * filterScale;
        var span = (int)Math.Ceiling(support) * 2 + 1;
        var bounds = new (int, int)[outSize];
        var weights = new double[outSize * span];

        for (var o = 0; o < outSize; o++)
        {
            var center = (o + 0.5) * scale;
            var start = Math.Max((int)(center - support + 0.5), 0);
            var end = Math.Min((int)(center + support + 0.5), inSize);
            var len = end - start;
            double sum = 0;
            for (var k = 0; k < len; k++)
            {
                var wgt = Lanczos((k + start - center + 0.5) / filterScale);
                weights[o * span + k] = wgt;
                sum += wgt;
            }
            if (sum != 0)
            {
                for (var k = 0; k < len; k++) weights[o * span + k] /= sum;
            }
            bounds[o] = (start, len);
        }
        return (bounds, weights, span);
    }

    private static double Lanczos(double x)
    {
        if (x <= -Support || x >= Support) return 0;
        return Sinc(x) * Sinc(x / Support);
    }

    private static double Sinc(double x)
    {
        if (x == 0) return 1;
        x *= Math.PI;
        return Math.Sin(x) / x;
    }
}
