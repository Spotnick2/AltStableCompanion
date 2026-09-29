namespace AltStableCompanion.Core;

/// <summary>
/// What the Roster needs to crop the texture back, plus the capture it came from. Written
/// beside the TGA as a JSON sidecar; the manifest is rebuilt from these.
/// </summary>
public sealed record CutoutMeta
{
    public int W { get; init; }
    public int H { get; init; }
    public int TexW { get; init; }
    public int TexH { get; init; }
    /// <summary>Fraction of SCREEN HEIGHT the figure's width occupies. Not a race height.</summary>
    public double? NativeW { get; init; }
    public double? NativeH { get; init; }
    public string? NativeUnit { get; init; }
    public int[]? NativePx { get; init; }
    /// <summary>Whose portrait this is: the manifest keys the entry by it.</summary>
    public string? Guid { get; init; }
    /// <summary>The capture's shot-1 epoch: "already converted" is guid + epoch.</summary>
    public long? Epoch { get; init; }

    /// <summary>
    /// A standing character is much taller than it is wide. Nearly square means something else
    /// survived the matte - a tooltip above the stage is the one that has happened. Worked out
    /// from the native size the sidecar keeps, so EVERY pass can say it, not only the one that
    /// wrote the cutout (the next pass follows within seconds).
    /// </summary>
    public bool NearlySquare => NativePx is [var w, var h] && w > h * 0.8;
}

public sealed record Cutout(RgbaImage Canvas, CutoutMeta Meta)
{
    public bool NearlySquare => Meta.NearlySquare;
}

/// <summary>One black/white pair to one cutout. Ported from make-cutout.py's convert().</summary>
public static class CutoutConverter
{
    /// <summary>How tall the figure is resampled to - far above what any scene draws it at.</summary>
    public const int TargetHeight = 512;

    /// <summary>A figure this much of the screen or more is the whole window, not a character.</summary>
    public const double MaxScreenFraction = 0.95;

    public static Cutout Convert(RgbaImage black, RgbaImage white, string? guid = null, long? epoch = null)
    {
        var cut = Matte.Compute(black, white);
        var nativeW = cut.Width;
        var nativeH = cut.Height;
        // The screenshot's own height is the yardstick - the image cannot be wrong about itself.
        var shotH = black.Height;

        // The render stage is a fixed size in UI units and lands around 0.6 of the frame.
        // Near 1.0 means the matte caught the whole window; and because heights are RELATIVE,
        // one of those filed draws every other character at half size.
        if (nativeH >= shotH * MaxScreenFraction)
        {
            throw new NotAPairException(
                $"the cutout is {nativeH} of {shotH} screen rows tall - the matte caught the whole window, not the character");
        }

        var scaled = Resampler.DownscaleToHeight(cut, TargetHeight);
        var canvas = new RgbaImage(Pot(scaled.Width), Pot(scaled.Height));
        for (var y = 0; y < scaled.Height; y++)
        {
            Buffer.BlockCopy(scaled.Pixels, scaled.Offset(0, y), canvas.Pixels, canvas.Offset(0, y), scaled.Width * 4);
        }

        var meta = new CutoutMeta
        {
            W = scaled.Width,
            H = scaled.Height,
            TexW = canvas.Width,
            TexH = canvas.Height,
            NativeUnit = "screen",
            NativeW = Math.Round(nativeW / (double)shotH, 5, MidpointRounding.ToEven),
            NativeH = Math.Round(nativeH / (double)shotH, 5, MidpointRounding.ToEven),
            NativePx = [nativeW, nativeH],
            Guid = guid,
            Epoch = epoch,
        };

        return new Cutout(canvas, meta);
    }

    /// <summary>The smallest power of two at least <paramref name="n"/>. WoW reloads those reliably.</summary>
    public static int Pot(int n)
    {
        var p = 1;
        while (p < n) p *= 2;
        return p;
    }
}
