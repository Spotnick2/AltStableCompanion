using System.Text.RegularExpressions;

namespace AltStableCompanion.Core;

/// <summary>
/// A character name as a file name: lowercase, every run of characters outside a-z0-9
/// becomes "-", trimmed. MUST equal Slug() in the addon's Plugins/Roster/AltStableRoster.lua
/// and slug() in make-cutout.py byte for byte - legacy manifest entries are keyed by it.
/// </summary>
public static partial class Slugger
{
    public static string Slug(string name) =>
        NonAlnum().Replace(name.ToLowerInvariant(), "-").Trim('-');

    /// <summary>
    /// The file name for a character's cutout: the slug, unless another character already
    /// owns that file - two characters can share a name - in which case the last six
    /// characters of the GUID are appended (docs/PORTRAIT-CONTRACT.md, section 2).
    ///
    /// A name with nothing in a-z0-9 (Cyrillic, Korean, Chinese) has an EMPTY slug, which would
    /// make the file ".tga". Such a character's file is named by its GUID instead; the manifest
    /// keys the entry by GUID, so the Roster never needs the name to find it.
    /// </summary>
    public static string FileBase(string name, string guid, Func<string, string?> ownerOf)
    {
        var slug = Slug(name);
        if (slug.Length == 0) return Slug(guid);
        var owner = ownerOf(slug);
        if (owner is null || owner == guid) return slug;
        // Lowercased FIRST: the GUID's hex is uppercase ("006B8614"), and the pattern only
        // keeps a-z0-9. make-cutout.py strips [^A-Za-z0-9] and lowercases after - same result.
        var alnum = NonAlnum().Replace(guid.ToLowerInvariant(), "");
        return $"{slug}-{alnum[Math.Max(0, alnum.Length - 6)..].ToLowerInvariant()}";
    }

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NonAlnum();
}
