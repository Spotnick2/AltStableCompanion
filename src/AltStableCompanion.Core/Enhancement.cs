using System.Security.Cryptography;
using System.Text;

namespace AltStableCompanion.Core;

/// <summary>
/// What an enhanced texture's sidecar says about how it was made and from what. The plain
/// portrait is named by the hash of its bytes: the one thing that changes exactly when the
/// portrait does.
/// </summary>
public sealed record EnhancementMeta
{
    public string? SourceHash { get; init; }
    public string? OutputHash { get; init; }
    public string? Style { get; init; }
    public string? Model { get; init; }
    public string? Effort { get; init; }
    public int Prompt { get; init; }
    public string? Signature { get; init; }
    public DateTime? Generated { get; init; }
}

/// <summary>The style presets a player can pick. The prompt's wording for each is the prompt's.</summary>
public static class EnhanceStyles
{
    public const string WowLike = "wow-like";
    public const string Realistic = "realistic";
    public const string Cartoonish = "cartoonish";
    public static readonly IReadOnlyList<string> All = [WowLike, Realistic, Cartoonish];

    public static string Normalize(string? name) => All.FirstOrDefault(s => s.Equals(name?.Trim(), StringComparison.OrdinalIgnoreCase)) ?? WowLike;
}

/// <summary>
/// A character the enhancement may spend a generation on: has a portrait resolved by GUID,
/// is of the minimum level, and is not hidden in any account. <see cref="Character"/> is the
/// roster record the prompt is written from.
/// </summary>
public sealed record EnhanceCandidate(string Guid, string FileBase, RosterCharacter Character);

/// <summary>
/// Who is eligible, from every account's store at once. Conflicts between accounts are
/// settled one way, always: hidden anywhere is hidden; the level is the highest seen; the
/// name, race, gender and class come from the most recently updated record.
/// </summary>
public static class Eligibility
{
    public static IReadOnlyList<EnhanceCandidate> Select(IReadOnlyList<PortraitStore> stores, int minLevel,
        Func<string, string?> fileBaseOf)
    {
        var hidden = new HashSet<string>(stores.SelectMany(s => s.HiddenCharacters), StringComparer.Ordinal);
        var merged = new Dictionary<string, RosterCharacter>(StringComparer.Ordinal);
        foreach (var c in stores.SelectMany(s => s.Roster))
        {
            if (!merged.TryGetValue(c.Guid, out var have))
            {
                merged[c.Guid] = c;
                continue;
            }
            var newest = c.Updated > have.Updated ? c : have;
            merged[c.Guid] = newest with { Level = Math.Max(c.Level, have.Level) };
        }
        var out_ = new List<EnhanceCandidate>();
        foreach (var c in merged.Values.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ThenBy(c => c.Guid, StringComparer.Ordinal))
        {
            if (hidden.Contains(c.Guid) || c.Level < minLevel) continue;
            // A name-only legacy portrait is nobody's in particular: not a base to build on.
            if (fileBaseOf(c.Guid) is not { } fileBase) continue;
            out_.Add(new EnhanceCandidate(c.Guid, fileBase, c));
        }
        return out_;
    }
}

/// <summary>
/// The words Codex is given. The attached screenshot cutout is the authority for everything
/// it shows - skin, hair, clothing, colours, what is in the hands - and the prompt says only
/// what a small screenshot cannot: the race's anatomy, the style, the framing, the format.
/// Measured on 2026-09-30: a prompt that put invented gear words over the reference gave a
/// black-robed blue troll; this one gave the character.
/// </summary>
public static class EnhancementPrompt
{
    /// <summary>Bumped when the words change enough that a picture made with the old ones is not this prompt's.</summary>
    public const int Version = 1;

    public static string Build(RosterCharacter c, string style)
    {
        var race = c.Race?.Trim() ?? "";
        var gender = c.Gender?.Trim() ?? "";
        var cls = c.Class?.Trim() ?? "";
        var who = string.Join(" ", new[] { gender, race, Class(cls) }.Where(w => w.Length > 0));
        if (who.Length == 0) who = "character";
        var sb = new StringBuilder();
        sb.AppendLine("Use the imagegen skill (the built-in image_gen tool) to generate ONE image.");
        sb.AppendLine();
        sb.AppendLine($"The attached image is the character: a screenshot cutout of a {who} from World of Warcraft. " +
                      "It is the authority for everything about them - copy it faithfully rather than inventing:");
        sb.AppendLine("- skin colour exactly as shown, face, eye colour, expression;");
        sb.AppendLine("- hair exactly as shown: colour, length and style, and how it sits with any headwear;");
        sb.AppendLine("- jewellery, tattoos, war paint and markings as shown;");
        sb.AppendLine("- the clothing and armour exactly as shown: the same garments, the same colours and patterns, the same amount of bare skin;");
        sb.AppendLine("- what is in the hands as shown, and nothing more: do not add weapons, magic effects, glows, pets or anything the screenshot does not show;");
        sb.AppendLine("- the standing pose, facing the viewer.");
        sb.AppendLine();
        if (Anatomy(race) is { } anatomy) sb.AppendLine($"{Cap(race)} anatomy, which the small screenshot may not make clear: {anatomy}.").AppendLine();
        sb.AppendLine(Style(style));
        sb.AppendLine();
        sb.AppendLine("Requirements:");
        sb.AppendLine("- Vertical full-body portrait, 1024x1536 pixels.");
        sb.AppendLine("- TRANSPARENT background: a PNG with an alpha channel, the character fully opaque, everything else fully transparent. No scenery, no floor, no ground shadow, no glow, no vignette.");
        sb.AppendLine("- The whole character inside the frame - top of the head to feet - with visible margin on every side; nothing touches the edges.");
        sb.AppendLine("- No text, no nameplate, no UI, no watermark.");
        sb.AppendLine("- Produce EXACTLY ONE image.");
        sb.AppendLine("- Do NOT edit, create, or modify any files.");
        sb.AppendLine("- After generating, report on its own line the EXACT absolute path of the generated image, prefixed with \"ARTIFACT_PATH: \".");
        return sb.ToString();
    }

    /// <summary>
    /// What a 167-pixel-wide cutout cannot say about a race. The Forever client's races; a
    /// race not here gets no line, which is safe - the reference still rules.
    /// </summary>
    public static string? Anatomy(string race) => race.ToLowerInvariant() switch
    {
        "troll" => "two toes on each foot, three fingers on each hand, long pointed ears, tusks at the mouth as small or large as in the screenshot, a tall lean build",
        "orc" => "green skin as in the screenshot, prominent lower tusks, a heavy muscular build, five fingers and five toes",
        "tauren" => "hooves instead of feet, a bovine head with a muzzle and horns, a tail, three fingers on each hand, a massive build",
        "scourge" or "undead" => "greyish undead skin, a gaunt build, exposed bone at the joints or spine only where the screenshot shows it, five fingers and five toes",
        "night elf" or "nightelf" => "long pointed ears, glowing eyes, long eyebrows, a tall slender build",
        "gnome" => "a very small build, about a third of a human's height, with a large head",
        "dwarf" => "a short, broad, stocky build",
        "human" => "an ordinary human build",
        _ => null,
    };

    private static string Style(string style) => EnhanceStyles.Normalize(style) switch
    {
        EnhanceStyles.Realistic => "Render them as a highly detailed photorealistic fantasy portrait with cinematic lighting: the same character seen as if real.",
        EnhanceStyles.Cartoonish => "Render them as a stylized cartoon fantasy portrait: bold outlines, saturated colours, exaggerated but recognisable features: the same character, drawn.",
        _ => "Render them as a World of Warcraft style fantasy portrait: semi-realistic, painterly, vibrant, heroic proportions, clean and detailed: the same character seen at higher quality.",
    };

    // The addon stores WoW's upper-case class token.
    private static string Class(string token) => token.ToLowerInvariant() switch
    {
        "" => "",
        "deathknight" => "Death Knight",
        "demonhunter" => "Demon Hunter",
        var t => char.ToUpperInvariant(t[0]) + t[1..],
    };

    private static string Cap(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
}

/// <summary>
/// What one attempt is FOR: the capture (the plain portrait's bytes and its epoch), the
/// character as the prompt describes them, the style, the model, the effort, the prompt's
/// version. Two attempts with the same signature are the same attempt, and only one is ever
/// launched. Level and gear are not in it: they change without the portrait changing.
/// </summary>
public static class EnhancementSignature
{
    public static string Compute(string guid, long? epoch, string sourceHash, RosterCharacter c, string style, string model, string effort) =>
        Compute(guid, epoch, sourceHash, c.Race, c.Gender, c.Class, style, model, effort);

    public static string Compute(string guid, long? epoch, string sourceHash, string? race, string? gender, string? cls,
        string style, string model, string effort)
    {
        var line = string.Join("|",
            guid.Trim(),
            epoch?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "undated",
            sourceHash.Trim().ToLowerInvariant(),
            Norm(race), Norm(gender), Norm(cls),
            EnhanceStyles.Normalize(style), Norm(model), Norm(effort),
            EnhancementPrompt.Version.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(line)));
    }

    /// <summary>SHA-256 of a file's bytes, lower-case hex: how a plain portrait and an enhanced one are named.</summary>
    public static string HashOf(string path)
    {
        using var s = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(s));
    }

    public static string HashOf(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static string Norm(string? s) => (s ?? "").Trim().ToLowerInvariant();
}

/// <summary>
/// From what Codex gave back to what the Roster draws. Three checks, each named, that say
/// the picture is shaped like a cutout - not that it is the right character, which is the
/// player's to see:
///   "transparent border": the outer two rows at the top and the outer two columns on each
///     side are transparent (the bottom may hold the feet: the model plants them there);
///   "enough figure": at least 5 % of the pixels are opaque (alpha 250 or more);
///   "standing figure": the figure's box is at least 1.15 times as tall as wide.
/// Then the same tail as the converter: crop to the figure, scale to the cutout height,
/// pad to a power-of-two canvas, top-left.
/// </summary>
public static class Enhancement
{
    public const string TransparentBorder = "transparent border";
    public const string EnoughFigure = "enough figure";
    public const string StandingFigure = "standing figure";
    public const int Opaque = 250;
    public const int Visible = 8;
    public const double MinAspect = 1.15;

    /// <summary>The name of the first check the picture fails, or null when it passes all three.</summary>
    public static string? Refuse(RgbaImage png)
    {
        for (var y = 0; y < Math.Min(2, png.Height); y++)
            for (var x = 0; x < png.Width; x++)
                if (png[x, y].A >= Visible) return TransparentBorder;
        for (var x = 0; x < png.Width; x++)
            if (x < 2 || x >= png.Width - 2)
                for (var y = 0; y < png.Height; y++)
                    if (png[x, y].A >= Visible) return TransparentBorder;

        long opaque = 0;
        for (var i = 3; i < png.Pixels.Length; i += 4) if (png.Pixels[i] >= Opaque) opaque++;
        if (opaque * 20 < (long)png.Width * png.Height) return EnoughFigure;

        var box = Bounds(png);
        if (box is null || box.Value.Height < box.Value.Width * MinAspect) return StandingFigure;
        return null;
    }

    /// <summary>The picture as a cutout canvas with its sidecar sizes, or a <see cref="ThumbnailException"/> naming the failed check.</summary>
    public static Cutout ToCutout(RgbaImage png, CutoutMeta meta)
    {
        if (Refuse(png) is { } why) throw new ThumbnailException(why);
        var box = Bounds(png)!.Value;
        var figure = png.Crop(box.X, box.Y, box.Width, box.Height);
        var scaled = Resampler.DownscaleToHeight(figure, CutoutConverter.TargetHeight);
        var canvas = new RgbaImage(CutoutConverter.Pot(scaled.Width), CutoutConverter.Pot(scaled.Height));
        for (var y = 0; y < scaled.Height; y++)
        {
            Buffer.BlockCopy(scaled.Pixels, scaled.Offset(0, y), canvas.Pixels, canvas.Offset(0, y), scaled.Width * 4);
        }
        return new Cutout(canvas, meta with { W = scaled.Width, H = scaled.Height, TexW = canvas.Width, TexH = canvas.Height, Shots = null });
    }

    // The smallest box holding every pixel that is visible at all: faint noise below
    // Visible is not the figure.
    private static (int X, int Y, int Width, int Height)? Bounds(RgbaImage img)
    {
        int minX = img.Width, minY = img.Height, maxX = -1, maxY = -1;
        for (var y = 0; y < img.Height; y++)
            for (var x = 0; x < img.Width; x++)
            {
                if (img.Pixels[img.Offset(x, y) + 3] < Visible) continue;
                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
            }
        return maxX < 0 ? null : (minX, minY, maxX - minX + 1, maxY - minY + 1);
    }
}
