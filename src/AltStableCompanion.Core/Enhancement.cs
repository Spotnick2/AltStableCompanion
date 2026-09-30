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

/// <summary>Codex's reasoning efforts, as its <c>model_reasoning_effort</c> takes them. Low is measured to be enough.</summary>
public static class EnhanceEfforts
{
    public const string Low = "low";
    public const string Medium = "medium";
    public const string High = "high";
    public static readonly IReadOnlyList<string> All = [Low, Medium, High];

    public static string Normalize(string? name) => All.FirstOrDefault(e => e.Equals(name?.Trim(), StringComparison.OrdinalIgnoreCase)) ?? Low;
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

/// <summary>Who may be enhanced this time - or nobody, and why.</summary>
public sealed record EligibilityResult(IReadOnlyList<EnhanceCandidate> Candidates, string? Refused = null);

/// <summary>
/// Who is eligible, from every account's roster at once. Conflicts between accounts are
/// settled one way, always: hidden anywhere is hidden; the level is the highest seen; the
/// name, race, gender and class come from the most recently updated record, and between
/// two updated at the same moment, the one from the file that sorts first. A snapshot with a
/// file that could not be read is not a view of every account: nobody is eligible from it,
/// because a launch cannot be taken back.
/// </summary>
public static class Eligibility
{
    public static EligibilityResult Select(SavedVariablesSnapshot snapshot, int minLevel, Func<string, string?> fileBaseOf)
    {
        if (snapshot.Skipped.Count > 0)
        {
            return new([], $"{snapshot.Skipped.Count} account file(s) could not be read this time ({Account(snapshot.Skipped[0])}): not knowing who is hidden is not permission");
        }
        // A roster table that stopped mid-way in any account: who it hides is not known either.
        if (snapshot.Rosters.FirstOrDefault(r => r.Problem is not null) is { } broken)
        {
            return new([], $"{Account(broken.Path)}'s roster could not be read ({broken.Problem}): not knowing who is hidden is not permission");
        }
        var hidden = new HashSet<string>(snapshot.Rosters.SelectMany(r => r.Hidden), StringComparer.Ordinal);
        var merged = new Dictionary<string, RosterCharacter>(StringComparer.Ordinal);
        foreach (var roster in snapshot.Rosters.OrderBy(r => r.Path, StringComparer.OrdinalIgnoreCase))
        {
            foreach (var c in roster.Characters)
            {
                if (!merged.TryGetValue(c.Guid, out var have))
                {
                    merged[c.Guid] = c;
                    continue;
                }
                // Strictly newer wins; a tie keeps the one from the file that sorted first.
                var newest = c.Updated > have.Updated ? c : have;
                merged[c.Guid] = newest with { Level = Math.Max(c.Level, have.Level) };
            }
        }
        var out_ = new List<EnhanceCandidate>();
        foreach (var c in merged.Values.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ThenBy(c => c.Guid, StringComparer.Ordinal))
        {
            if (hidden.Contains(c.Guid) || c.Level < minLevel) continue;
            // The prompt is written from these: a record without them is not one to spend on.
            if (string.IsNullOrWhiteSpace(c.Class) || string.IsNullOrWhiteSpace(c.Race) || string.IsNullOrWhiteSpace(c.Gender)) continue;
            // A name-only legacy portrait is nobody's in particular: not a base to build on.
            if (fileBaseOf(c.Guid) is not { } fileBase) continue;
            out_.Add(new EnhanceCandidate(c.Guid, fileBase, c));
        }
        return new(out_);
    }

    // The account folder's name, for a message: WTF\Account\<name>\SavedVariables\AltStable.lua.
    private static string Account(string storePath) =>
        Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(storePath))) ?? storePath;
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
    /// <summary>
    /// Bumped when the words change enough that a picture made with the old ones is not this
    /// prompt's; the version is in the signature, so every eligible character is made again.
    /// 2 (2026-09-30): version 1 said "copy it faithfully" six times and named the style once,
    /// and the pictures were the game model with better shading, realistic included. Now the
    /// screenshot says WHO and the style says HOW, and the picture is made from scratch.
    /// </summary>
    public const int Version = 2;

    public static string Build(RosterCharacter c, string style)
    {
        var race = c.Race?.Trim() ?? "";
        // The race as a reader would say it: the client's own display name when the addon
        // kept it ("Windshaper Skyborne"), else the token spelled out ("Night Elf").
        var raceWords = string.IsNullOrWhiteSpace(c.RaceName) ? RaceWords(race) : c.RaceName.Trim();
        var gender = c.Gender?.Trim() ?? "";
        var cls = c.Class?.Trim() ?? "";
        var who = string.Join(" ", new[] { gender, raceWords, Class(cls) }.Where(w => w.Length > 0));
        if (who.Length == 0) who = "character";
        // The shape (the tool named, not the skill; identity kept, rendering replaced; a bounded
        // natural pose; margins the checks in Enhancement.Inspect will accept) was reviewed by
        // Codex on 2026-09-30 after version 1's pictures came back as the game model.
        var sb = new StringBuilder();
        sb.AppendLine("Use the built-in image_gen tool to generate exactly ONE image from the attached character reference, with a transparent background. Make one generation call; do not generate variants or retry.");
        sb.AppendLine();
        sb.AppendLine($"Create a new full-body image of this World of Warcraft character: a {who}.");
        sb.AppendLine();
        sb.AppendLine("REFERENCE ROLE");
        sb.AppendLine("The attachment is a small, low-resolution screenshot of a game model. Use it to identify the character and their equipment. Create a fresh depiction in the requested style; do not reproduce its low-resolution textures, polygonal surfaces, baked-in lighting or stiff model pose.");
        sb.AppendLine();
        sb.AppendLine("STYLE");
        sb.AppendLine(Style(style));
        sb.AppendLine();
        sb.AppendLine("IDENTITY AND EQUIPMENT");
        sb.AppendLine("Preserve the character's recognisable design:");
        sb.AppendLine("- Skin colour, facial features, eye colour and expression, as far as visible.");
        sb.AppendLine("- Hair colour, length, hairstyle and overall hair silhouette, including its relationship to headwear.");
        sb.AppendLine("- Visible jewellery, tattoos, war paint and other markings.");
        sb.AppendLine("- The same clothing and armour pieces, their shapes, colours, patterns and coverage. Keep bare skin exposed in the same locations. Do not add layers or enlarge armour.");
        sb.AppendLine("- The same visible weapons and held objects: preserve their number, recognisable shape, colours, relative size and which hand holds each. Empty hands stay empty.");
        sb.AppendLine("- Visible accessories elsewhere on the body as well.");
        sb.AppendLine();
        sb.AppendLine("Race and class provide context, not permission to invent equipment. Do not add weapons, shields, armour pieces, capes, ornaments, pets or magical effects. Where the screenshot is unclear, use a simple interpretation consistent with what is visible.");
        sb.AppendLine();
        if (Anatomy(race) is { } anatomy)
        {
            sb.AppendLine($"{RaceWords(race)} anatomy, which the small screenshot may not make clear: {anatomy}. This applies beneath the existing clothing and equipment: do not remove or alter clothing to expose it.").AppendLine();
        }
        else if (race.Length > 0)
        {
            // A race the model has never heard of (Forever's Skyborne): the picture is all there is.
            sb.AppendLine($"The {raceWords} may be a race you do not know: take its anatomy - ears, hands, feet, horns, wings, tail, tusks - exactly from the screenshot, and invent nothing.").AppendLine();
        }
        sb.AppendLine("POSE");
        sb.AppendLine("A relaxed, confident standing pose with a small natural weight shift, both feet down, face and torso toward the viewer; the race's natural posture kept, including any hunch. Relax the screenshot's stiff arms while keeping held items in their original hands and their designs visible; arms and equipment reasonably close to the body. No action pose, lunge, crouch, jump, spread arms or extreme perspective. The silhouette stays taller than it is wide.");
        sb.AppendLine();
        sb.AppendLine("COMPOSITION AND OUTPUT");
        sb.AppendLine("- One character, one view, one vertical 1024x1536 PNG.");
        sb.AppendLine("- The complete character and all equipment, from the highest hair, headwear or weapon tip to the lowest foot or item tip.");
        sb.AppendLine("- The complete silhouette centred, occupying about 80-84% of the canvas height, with at least 8% of the canvas left empty along EACH edge. The margins include hair strands, ears, shoulder pieces, clothing and weapons: nothing touches or crosses an edge.");
        sb.AppendLine("- The character prominent, not a tiny figure in empty space. Fit broad equipment by adjusting the pose and the framing, without cropping, shrinking items or changing their design.");
        sb.AppendLine("- A real transparent background with an alpha channel: empty space, including gaps between limbs and equipment, fully transparent; the character opaque, with normal antialiasing at the silhouette's edges.");
        sb.AppendLine("- No scenery, backdrop, floor, ground shadow, reflection, haze, aura, glow around the figure, vignette, border or checkerboard pattern. Lighting and shadows describe the character's surfaces only.");
        sb.AppendLine("- No text, nameplate, UI, watermark, extra views or duplicate figures.");
        sb.AppendLine();
        sb.AppendLine("EXECUTION AND DELIVERY");
        sb.AppendLine("The image tool may create its normal output file; leave it where the tool put it. Do not otherwise create, edit, copy or modify files, and do not use scripts or external image-generation services.");
        sb.AppendLine("After a successful generation, return this line with the actual absolute local path the tool reported:");
        sb.AppendLine("ARTIFACT_PATH: <absolute path of the generated image>");
        sb.AppendLine("If the generation fails or no local path is available, say so plainly; do not invent a path or emit an ARTIFACT_PATH line.");
        return sb.ToString();
    }

    /// <summary>The race token (UnitRace's fileName, what the addon stores) as words: "NightElf" is "Night Elf", "Scourge" is "Undead".</summary>
    public static string RaceWords(string token) => token.Trim().ToLowerInvariant() switch
    {
        "" => "",
        "nightelf" or "night elf" => "Night Elf",
        "scourge" or "undead" => "Undead",
        "bloodelf" => "Blood Elf",
        var t => Cap(t),
    };

    /// <summary>
    /// What a 167-pixel-wide cutout cannot say about a race. The Classic races the model
    /// knows; a race not here (Forever's own Skyborne) gets the "take it from the picture"
    /// line instead - the reference still rules.
    /// </summary>
    public static string? Anatomy(string race) => race.Trim().ToLowerInvariant() switch
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

    // The style must be visible at thumbnail size: "subtle" was the owner's word for a failure.
    private static string Style(string style) => EnhanceStyles.Normalize(style) switch
    {
        EnhanceStyles.Realistic => "Photorealistic: this character as a living person or fantasy being, photographed with studio-quality lighting. Preserve their race-specific anatomy and proportions, including nonhuman features. Real skin with pores and fine lines, real hair with individual strands, fabric weave, worn leather and scuffed metal; cinematic lighting with soft shadows on the figure itself. Make the existing costume and equipment look physically constructed, with visible craftsmanship - stitching, buckles, rivets, engraving - and honest wear. It must not look like a 3D game model, a painting, or a human dressed as a different race.",
        EnhanceStyles.Cartoonish => "A polished animated-series character illustration: bold clean outlines, flat saturated colours and crisp cel shading. Simplify surface textures into clear graphic shapes while preserving the recognisable face, hairstyle, markings, costume patterns and equipment. Expressive, moderately exaggerated features, the race's recognisable proportions kept, a standing silhouette taller than it is wide. One finished full-body character in one view, with no turnaround panels or inset details.",
        _ => "A finished, hand-painted World of Warcraft promotional character illustration: bold sculpted forms, expressive brushwork, rich colour, and dramatic light and shadow across the figure. Make the transformation obvious at thumbnail size through strong painted volumes and clear material separation, with dimensional cloth folds, substantial metal, textured leather and flowing locks of hair. Preserve the character's recognisable WoW race proportions and existing costume shapes. The result should read as polished fantasy key art, with visible painterly decisions throughout, rather than an in-game render with improved shading. Keep the original equipment design and colours.",
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

/// <summary>What one look at a picture found: enough to refuse it or to cut it out, in one pass.</summary>
public sealed record Inspection(bool BorderHit, long Opaque, int MinX, int MinY, int MaxX, int MaxY)
{
    public bool AnyVisible => MaxX >= 0;
    public int Width => MaxX - MinX + 1;
    public int Height => MaxY - MinY + 1;
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

    /// <summary>One pass over the pixels: the border, the opaque count, and the box of everything visible.</summary>
    public static Inspection Inspect(RgbaImage png)
    {
        var borderHit = false;
        long opaque = 0;
        int minX = png.Width, minY = png.Height, maxX = -1, maxY = -1;
        var pixels = png.Pixels;
        var w = png.Width;
        for (var y = 0; y < png.Height; y++)
        {
            var row = y * w * 4;
            for (var x = 0; x < w; x++)
            {
                var a = pixels[row + x * 4 + 3];
                if (a >= Opaque) opaque++;
                if (a < Visible) continue;
                // The top two rows and the outer two columns must be clear; the bottom may hold the feet.
                if (y < 2 || x < 2 || x >= w - 2) borderHit = true;
                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
            }
        }
        return new Inspection(borderHit, opaque, minX, minY, maxX, maxY);
    }

    /// <summary>The name of the first check the picture fails, or null when it passes all three.</summary>
    public static string? Refuse(RgbaImage png) => Refuse(png, Inspect(png));

    public static string? Refuse(RgbaImage png, Inspection seen)
    {
        if (seen.BorderHit) return TransparentBorder;
        if (seen.Opaque * 20 < (long)png.Width * png.Height) return EnoughFigure;
        if (!seen.AnyVisible || seen.Height < seen.Width * MinAspect) return StandingFigure;
        return null;
    }

    /// <summary>The picture as a cutout canvas with its sidecar sizes, or a <see cref="ThumbnailException"/> naming the failed check.</summary>
    public static Cutout ToCutout(RgbaImage png, CutoutMeta meta)
    {
        var seen = Inspect(png);
        if (Refuse(png, seen) is { } why) throw new ThumbnailException(why);
        var figure = png.Crop(seen.MinX, seen.MinY, seen.Width, seen.Height);
        var (canvas, scaled) = CutoutConverter.OnCanvas(figure);
        return new Cutout(canvas, meta with { W = scaled.Width, H = scaled.Height, TexW = canvas.Width, TexH = canvas.Height, Shots = null });
    }
}
