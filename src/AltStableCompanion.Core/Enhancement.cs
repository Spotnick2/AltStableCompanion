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
/// The words Codex is given. The attached cutout is the design reference - who the character
/// is: race, face, hair, clothing, colours, what is in the hands - and the prompt asks for the
/// sculpt and the rendering to be reinterpreted in a named art direction, with the race's
/// anatomy, the guards (nothing added, one image, nothing around the figure) and the format.
/// Measured on 2026-09-30: words that made the reference "the authority" to copy gave the
/// game model back; words that put invented gear over it gave another character.
/// </summary>
public static class EnhancementPrompt
{
    /// <summary>
    /// Bumped when the words change enough that a picture made with the old ones is not this
    /// prompt's; the version is in the signature, so every eligible character is made again.
    /// 2 (2026-09-30): version 1 said "copy it faithfully" six times and named the style once,
    /// and the pictures were the game model with better shading, realistic included. Now the
    /// design is preserved and the sculpt and the rendering are reinterpreted, in words
    /// measured on a gnome against seven other shapes.
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
        // The shape and most of the words are the ones GPT-6-Astra revised for the owner on
        // 2026-09-30 after seeing the results, and that measured best on a gnome through this
        // pipeline (a sculpted brow, cheekbones and jaw, eyes seated in the face; the hair,
        // outfit and wrench untouched). Its one gnome-specific sentence is made general here.
        // "Copy it faithfully" kept the game model; a fresh depiction lost the character; this
        // keeps the design and reinterprets the sculpt and the rendering.
        var w = Words(style);
        var sb = new StringBuilder();
        sb.AppendLine($"Use the built-in image_gen tool. Use the attached character as the design reference and generate a distinctly {w.Interpretation} interpretation.");
        sb.AppendLine();
        sb.AppendLine($"The character is a {who}.");
        sb.AppendLine();
        sb.AppendLine("Preserve the recognizable character: race, gender, apparent age, expression, skin tone, hair color and hairstyle, eye color, clothing, equipment, color palette, and pose.");
        sb.AppendLine();
        sb.AppendLine(w.Reinterpret);
        sb.AppendLine();
        if (Anatomy(race) is { } anatomy)
        {
            sb.AppendLine($"{RaceWords(race)} anatomy, which the small reference may not make clear: {anatomy}. It applies beneath the clothing and equipment; do not remove or alter them to show it.").AppendLine();
        }
        else if (race.Length > 0)
        {
            // A race the model has never heard of: the reference is what there is to go on.
            sb.AppendLine($"The {raceWords} may be a race you do not know: take its anatomy - ears, hands, feet, horns, wings, tail, tusks - from the reference and keep it; do not substitute the anatomy of a race you know.").AppendLine();
        }
        sb.AppendLine(w.Look);
        sb.AppendLine();
        sb.AppendLine($"The transformation must be apparent at thumbnail size through facial structure, anatomy, hair, lighting, and material depth - not merely sharper textures. {w.Avoid}");
        sb.AppendLine();
        sb.AppendLine("Keep the original costume and equipment design. Do not add armor, weapons, pets, companions, spell effects, glows, auras, scars, dirt, or age; one figure only. Show the full character, including all hair, boots, and equipment, with comfortable margins.");
        sb.AppendLine();
        sb.AppendLine("Output one image, 1024x1536, as a PNG on a genuinely transparent background. No scenery, floor, ground plane, ground shadow, glow, halo, vignette, text, or logos; lighting and shadows describe the character's surfaces only. Generate exactly one image in one call: do not generate variants, retry, or post-process the result with scripts, and do not create, edit, or modify any other files. After generating, report on its own line the exact absolute path of the generated image, prefixed with \"ARTIFACT_PATH: \". If the generation fails or no local path is available, say so plainly; do not invent a path or emit an ARTIFACT_PATH line.");
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
    /// knows, and Forever's Skyborne (a high elf, per the owner); a race not here gets the
    /// "take it from the picture" line instead - the reference still rules.
    /// </summary>
    public static string? Anatomy(string race) => race.Trim().ToLowerInvariant() switch
    {
        "troll" => "two toes on each foot, three fingers on each hand, long pointed ears, tusks at the mouth as small or large as in the reference, a tall lean build",
        "orc" => "green skin as in the reference, prominent lower tusks, a heavy muscular build, five fingers and five toes",
        "tauren" => "hooves instead of feet, a bovine head with a muzzle and horns, a tail, three fingers on each hand, a massive build",
        "scourge" or "undead" => "greyish undead skin, a gaunt build, exposed bone at the joints or spine only where the reference shows it, five fingers and five toes",
        "night elf" or "nightelf" => "long pointed ears, glowing eyes, long eyebrows, a tall slender build",
        "gnome" => "a very small build, about a third of a human's height, with a large head",
        "dwarf" => "a short, broad, stocky build",
        "human" => "an ordinary human build",
        // Forever's own race; the owner: anatomically a high elf.
        var t when t.Contains("skyborne") => "a high elf's anatomy: tall and slender, long pointed ears, fine features, five fingers and five toes",
        _ => null,
    };

    // The style must be visible at thumbnail size: "subtle" was the owner's word for a failure.
    // Interpretation: what to generate; Reinterpret: how far from the reference and what stays;
    // Look: the surfaces and the light; Avoid: the failure modes seen.
    private sealed record StyleWords(string Interpretation, string Reinterpret, string Look, string Avoid);

    private const string RaceFeatures = "Keep the race's signature features - stature, ears, nose, tusks, horns, head-to-body proportions - and the character's expressive personality.";

    private static StyleWords Words(string style) => EnhanceStyles.Normalize(style) switch
    {
        EnhanceStyles.Realistic => new(
            "photorealistic, live-action",
            "The finished image should look like a photograph of a living fantasy character. Reinterpret the anatomy and surfaces substantially enough to achieve photographic realism. You may adjust eye size, eyelids, facial planes, and small anatomical proportions while retaining the character's distinctive features and racial silhouette. Do not preserve cartoon geometry simply because it appears in the reference. "
            + RaceFeatures + " Make those features feel like convincing living anatomy, with underlying bone, cartilage, muscle, and soft tissue; do not normalize the character's proportions toward an average human's.",
            "Render eyes with believable eyeball size, detailed irises, moist tear lines, natural eyelid thickness, and subtle reflections; preserve the original iris color without making the eyes look like glass marbles. Use natural skin with subtle pores, fine facial hair, gentle color variation, and slight asymmetry; realism comes from anatomy, materials, and light, not from wrinkles, scars, dirt, or aging. "
            + "Translate the hairstyle into real individual hairs and physically plausible styled locks, preserving its recognizable silhouette and color while allowing natural strand irregularity; avoid solid sculpted spikes or a synthetic wig appearance. "
            + "Translate the original outfit into actual constructed garments: appropriate fabric weave, seams, leather thickness, stitching, folds caused by gravity, and believable metal fittings; preserve the design and the existing wear level. "
            + "Use photographic portrait lighting: a broad directional key light, soft fill, natural shadow transitions, and restrained highlights. Keep the whole character clearly focused, with realistic lens perspective and balanced exposure.",
            "Avoid glossy CGI skin, illustration, game-render shading, and exaggerated cinematic bloom. Aim for a convincing live-action fantasy film character photographed on set."),
        EnhanceStyles.Cartoonish => new(
            "animated-feature",
            "Reinterpret the rendering substantially into a polished animated-feature aesthetic: simplify the facial sculpt into clear, appealing planes and expressive, moderately exaggerated features while preserving the character's identity and racial proportions. " + RaceFeatures,
            "Use bold clean outlines, flat saturated colors, crisp cel shading, and surface textures simplified into clear graphic shapes, with the costume patterns and equipment kept recognizable.",
            "Avoid a low-poly game appearance, photorealism, turnaround panels, and inset details."),
        _ => new(
            "World of Warcraft cinematic",
            "Reinterpret the facial sculpt and rendering substantially. You may adjust eye size, facial planes, and anatomical details to achieve Blizzard's pre-rendered Warcraft cinematic aesthetic while preserving the character's identity and racial proportions. Use a more defined brow, cheekbones, eyelids, and jaw; eyes seated convincingly within the face; and nuanced skin shading. " + RaceFeatures,
            "Create cinematic depth through a directional soft key light, restrained fill, deeper natural shadows, and a subtle rim light. Give skin believable subsurface scattering and fine texture without excessive wrinkles. Render hair as carefully groomed strands and locks with natural variation. Give leather, cloth, and metal distinct physical responses to light.",
            "Avoid a cute animated-family-film appearance, toy-like surfaces, and flat character-preview lighting."),
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
///     side hold nothing opaque and no run of visible pixels - a hair wisp fading out there is
///     not a cut figure, a cropped arm is (the bottom may hold the feet: the model plants them there);
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
    /// <summary>Visible pixels in the border band beyond this share of it are a cut figure, feathered or not; wisps measured 3 and 57 of 8192.</summary>
    public const double BorderRun = 0.015;

    /// <summary>One pass over the pixels: the border, the opaque count, and the box of everything visible.</summary>
    public static Inspection Inspect(RgbaImage png)
    {
        var borderHit = false;
        long opaque = 0;
        long onBorder = 0;
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
                // The top two rows and the outer two columns must hold nothing OPAQUE: a cut
                // figure does, a hair wisp fading out at the edge does not. Measured on nine
                // pictures of 2026-09-30: the tool frames the figure as it likes, whatever
                // margin the prompt asks for, and two good pictures had 3 and 57 faint pixels
                // there and no opaque one. The bottom may hold the feet.
                if (y < 2 || x < 2 || x >= w - 2)
                {
                    if (a >= Opaque) borderHit = true;
                    onBorder++;
                }
                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
            }
        }
        // A figure cropped at the edge and feathered there is not opaque in the band, but it
        // fills a run of it; a wisp is a few pixels.
        var band = 2L * w + 4L * Math.Max(0, png.Height - 2);
        if (onBorder > band * BorderRun) borderHit = true;
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
