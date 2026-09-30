using AltStableCompanion.Core;
using Avalonia.Media;

namespace AltStableCompanion.App;

/// <summary>
/// What each skin is made of: the four surfaces every colour in the window comes from. The
/// numbers START from the addon's (Skin.lua: tint, pane, data) and are then tuned by eye over
/// a desktop - the addon's glass sits over a dark 3D scene with layers this window does not
/// have, so the same numbers do not look the same. What is kept from the addon is the shape
/// of it: Clear is lighter and more see-through than Smoked, the reading surface (the list)
/// is opaque in both, and the rim is the same light line on both.
/// </summary>
/// <param name="Glass">The window shows what is behind it, blurred, under these colours.</param>
/// <param name="Body">The window itself. Translucent for glass.</param>
/// <param name="Pane">Panels that text sits on. Dense enough to read on over anything.</param>
/// <param name="Data">The list. Opaque, as in the addon.</param>
/// <param name="Rim">The line around panels and the list.</param>
internal sealed record SkinPalette(bool Glass, Color Body, Color Pane, Color Data, Color Rim)
{
    public static readonly SkinPalette Flat = new(false,
        Color.Parse("#FF0E1420"), Color.Parse("#FF151D2B"), Color.Parse("#FF0B1018"), Color.Parse("#FF2B3850"));

    // Skin.lua clear: tint (0.13, 0.16, 0.22) @ 0.24, pane (0.04, 0.05, 0.07) @ 0.62, data opaque.
    // The body is denser than the addon's 0.24: a white desktop shows through 0.24 as grey.
    public static readonly SkinPalette Clear = new(true,
        Color.Parse("#8C212938"), Color.Parse("#B30A0D12"), Color.Parse("#FF0D0F14"), Color.Parse("#40FFFFFF"));

    // Skin.lua smoked: tint (0.05, 0.06, 0.08) @ 0.62, pane (0.03, 0.03, 0.04) @ 0.80, data opaque.
    public static readonly SkinPalette Smoked = new(true,
        Color.Parse("#B80D0F14"), Color.Parse("#D908080A"), Color.Parse("#FF0A0A0D"), Color.Parse("#33FFFFFF"));

    public static SkinPalette Of(string skin) => Skins.Normalize(skin) switch
    {
        Skins.Smoked => Smoked,
        Skins.Flat => Flat,
        _ => Clear,
    };

    /// <summary>The same skin with nothing behind it: what it wears when there is no blur to be had.</summary>
    public SkinPalette Opaque() => this with
    {
        Glass = false,
        Body = Over(Body, Flat.Body),
        Pane = Over(Pane, Flat.Body),
        Rim = Over(Rim, Flat.Body),
    };

    // The colour, laid over an opaque ground.
    private static Color Over(Color top, Color ground)
    {
        var a = top.A / 255.0;
        return Color.FromRgb(
            (byte)Math.Round(top.R * a + ground.R * (1 - a)),
            (byte)Math.Round(top.G * a + ground.G * (1 - a)),
            (byte)Math.Round(top.B * a + ground.B * (1 - a)));
    }
}
