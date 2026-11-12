using System.Globalization;
using System.Text.RegularExpressions;

namespace AltStableCompanion.Core;

/// <summary>
/// Which of a character's capture and armory render is its portrait (docs/PORTRAIT-CONTRACT.md,
/// section 4, amended by AltStable#310). A capture is sharper than a render (a render's figure
/// is about half a capture's height) and may have an enhanced picture, so a capture stays the
/// portrait for as long as it still shows the character:
/// <list type="number">
/// <item>No converted capture: the render.</item>
/// <item>Changed since the capture? Gear: the capture record's <c>look</c> against the worn gear
/// of the character's most recently updated roster record, slot by slot (a slot one side lacks
/// is not a change). Appearance: <c>lookAt</c> later than the capture's <c>epoch</c>.</item>
/// <item>Not changed: the capture.</item>
/// <item>Changed: the render once it shows the change - its <c>Last-Modified</c> minus 7 hours
/// is at least the change's time, the latest of those that apply: <c>gearAt</c> for an item
/// that differs, <c>gearReadAt</c> (else <c>lastUpdate</c>) when only a helm's or cloak's
/// visibility differs, <c>lookAt</c> for the appearance. Until then the capture.</item>
/// <item>Cannot tell (no <c>look</c> on the capture's record, no gear in the roster, no time for
/// the change, an account file that could not be read): the old estimate,
/// <see cref="Armory.RenderWins"/>.</item>
/// </list>
/// </summary>
public sealed partial class PortraitJudge
{
    /// <summary>Nothing known of anyone: case 5 for every character, the old estimate.</summary>
    public static readonly PortraitJudge Unknown = new(new Dictionary<string, RosterCharacter>(), new Dictionary<(string, long), string>());

    /// <summary>The slots a portrait shows that the roster has, by slot id, with the roster's key (<c>AltStable.GEAR_SLOTS</c>). Not the shirt (4) or the tabard (19): the roster does not keep them.</summary>
    public static readonly IReadOnlyList<(int Slot, string Key)> Slots =
    [
        (1, "head"), (3, "shoulder"), (5, "chest"), (6, "waist"), (7, "legs"), (8, "feet"),
        (9, "wrist"), (10, "hands"), (15, "back"), (16, "mainhand"), (17, "offhand"),
    ];

    private const int Head = 1, Back = 15;
    private const string Hidden = "h", Empty = "0";

    private readonly IReadOnlyDictionary<string, RosterCharacter> _roster;
    private readonly IReadOnlyDictionary<(string Guid, long Epoch), string> _looks;

    private PortraitJudge(IReadOnlyDictionary<string, RosterCharacter> roster, IReadOnlyDictionary<(string Guid, long Epoch), string> looks)
    {
        _roster = roster;
        _looks = looks;
    }

    /// <summary>
    /// The judge for this snapshot. An account file that could not be read, or a roster that
    /// stopped mid-way, is not a view of what anyone wears: <see cref="Unknown"/>.
    /// </summary>
    public static PortraitJudge From(SavedVariablesSnapshot snapshot)
    {
        if (snapshot.Skipped.Count > 0 || snapshot.Rosters.Any(r => r.Problem is not null)) return Unknown;
        // The most recently updated record (a tie keeps the file that sorts first): the gear
        // and its times are that record's, all of them, as they were read together.
        var roster = new Dictionary<string, RosterCharacter>(StringComparer.Ordinal);
        foreach (var r in snapshot.Rosters.OrderBy(r => r.Path, StringComparer.OrdinalIgnoreCase))
        {
            foreach (var c in r.Characters)
            {
                if (!roster.TryGetValue(c.Guid, out var have) || c.Updated > have.Updated) roster[c.Guid] = c;
            }
        }
        // Each capture's look, by its shot-1 epoch: what a converted capture's sidecar keeps.
        var looks = new Dictionary<(string, long), string>();
        foreach (var record in snapshot.Stores.SelectMany(s => s.Renders))
        {
            if (record is { Shot: 1, Epoch: { } epoch, Look: { Length: > 0 } look }) looks.TryAdd((record.Guid, epoch), look);
        }
        return new PortraitJudge(roster, looks);
    }

    /// <summary>
    /// The worn gear in a capture's <c>look</c> form, <c>slot:value;...</c>: the item id, <c>0</c>
    /// for an empty slot, <c>h</c> for a helm or cloak equipped while <c>hidehelm</c> /
    /// <c>hidecloak</c> is 1. Null when the record has no gear at all.
    /// </summary>
    public static string? GearOf(Func<string, double?> number)
    {
        var hideHelm = number("hidehelm") == 1;
        var hideCloak = number("hidecloak") == 1;
        var parts = new List<string>();
        foreach (var (slot, key) in Slots)
        {
            if (number("gearid_" + key) is not double id || id < 0 || id > long.MaxValue) continue;
            var item = (long)id;
            var value = item == 0 ? Empty
                : (slot == Head && hideHelm) || (slot == Back && hideCloak) ? Hidden
                : item.ToString(CultureInfo.InvariantCulture);
            parts.Add(slot.ToString(CultureInfo.InvariantCulture) + ":" + value);
        }
        return parts.Count == 0 ? null : string.Join(";", parts);
    }

    /// <summary>Whether the render, not the capture of <paramref name="captureEpoch"/>, is the character's portrait.</summary>
    public bool RenderWins(string guid, long? captureEpoch, CutoutMeta render)
    {
        if (Armory.AsOf(render) is not { } asOf) return false;
        if (captureEpoch is not { } epoch) return true;
        var (known, changedAt) = Changed(guid, epoch);
        if (!known) return Armory.RenderWins(epoch, render);
        return changedAt is { } at && asOf >= at;
    }

    // Known: whether the character changed since the capture, and if so when. Not known: case 5.
    private (bool Known, DateTimeOffset? ChangedAt) Changed(string guid, long epoch)
    {
        if (!_looks.TryGetValue((guid, epoch), out var look) || !_roster.TryGetValue(guid, out var c) || c.Gear is not { } gear)
        {
            return (false, null);
        }
        var captured = Parse(look);
        var worn = Parse(gear);
        long when = 0;
        foreach (var (slot, _) in Slots)
        {
            if (!captured.TryGetValue(slot, out var then) || !worn.TryGetValue(slot, out var now) || then == now) continue;
            // An item on both sides, hidden on one: the same item, shown or not.
            var shownOnly = (then == Hidden && now != Empty) || (now == Hidden && then != Empty);
            var at = shownOnly ? First(c.GearReadAt, c.Updated) : First(c.GearAt, c.Updated);
            if (at == 0) return (false, null);
            // Seen at or before the capture: the roster's record is older than the capture,
            // which read what was worn itself. Not a change since it. So a render can only
            // win with a change after the capture, and a render from before it never does.
            if (at <= epoch) continue;
            when = Math.Max(when, at);
        }
        var lookAt = ArmoryEligibility.Seconds(c.LookAt);
        if (lookAt > epoch) when = Math.Max(when, lookAt);
        return (true, when == 0 ? null : DateTimeOffset.FromUnixTimeSeconds(when));
    }

    private static long First(long time, long otherwise) =>
        ArmoryEligibility.Seconds(time) is > 0 and var t ? t : ArmoryEligibility.Seconds(otherwise);

    // "slot:value;slot:value": as the addon reads it (Capture.lua, LookDiff).
    private static Dictionary<int, string> Parse(string look)
    {
        var slots = new Dictionary<int, string>();
        foreach (Match m in SlotValue().Matches(look))
        {
            if (int.TryParse(m.Groups[1].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out var slot)) slots[slot] = m.Groups[2].Value;
        }
        return slots;
    }

    [GeneratedRegex(@"(\d+):(\w+)")]
    private static partial Regex SlotValue();
}
