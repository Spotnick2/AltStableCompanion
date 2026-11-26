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
/// <item>Changed since the capture? Gear: what the portrait shows against the character's most
/// recently updated roster record, slot by slot (a slot one side lacks is not a change). That
/// is the capture's <c>drawnLook</c> against the roster's drawn look (<see cref="DrawnGearOf"/>,
/// AltStable#497), since the portrait draws transmogs and hidden slots; a capture without a
/// <c>drawnLook</c> is its <c>look</c> against the equipped gear (<see cref="GearOf"/>).
/// Appearance: <c>lookAt</c> later than the capture's <c>epoch</c>.</item>
/// <item>Not changed: the capture.</item>
/// <item>Changed: the render once it is newer than the capture - its <c>Last-Modified</c> minus
/// 7 hours is after the capture's <c>epoch</c> (<see cref="Armory.RenderWins"/>). Not once it
/// shows the latest change: a character still being geared up changes again before each new
/// render lands, so its capture of last week stayed while a newer render was already closer
/// to it, and a render that had won went back to the capture at the next change (the
/// owner's Kaleidria and Drucida, 14 November). A change is one dated after the capture: a
/// drawn difference's <c>drawnLookAt</c>; else a differing item's <c>gearAt</c>, a helm's or
/// cloak's visibility's <c>gearReadAt</c> (else <c>lastUpdate</c>); the appearance's
/// <c>lookAt</c>.</item>
/// <item>Cannot tell (no <c>look</c> on the capture's record, no gear in the roster, no time for
/// the change, an account file that could not be read, a capture's <c>drawnLook</c> with no
/// drawn reading in the roster (no <c>drawnLookAt</c>), a drawn look read in another saved
/// outfit than the capture's - Forever switches outfits by itself, by weather and time of day,
/// at random among those that fit, so there is no verdict about the gear): the old estimate,
/// <see cref="Armory.RenderWins"/>.</item>
/// </list>
/// </summary>
public sealed partial class PortraitJudge
{
    /// <summary>Nothing known of anyone: case 5 for every character, the old estimate.</summary>
    public static readonly PortraitJudge Unknown = new(new Dictionary<string, RosterCharacter>(), new Dictionary<(string, long), Captured>());

    /// <summary>The slots a portrait shows that the roster has, by slot id, with the roster's key (<c>AltStable.GEAR_SLOTS</c>). Not the shirt (4) or the tabard (19): the roster does not keep them.</summary>
    public static readonly IReadOnlyList<(int Slot, string Key)> Slots =
    [
        (1, "head"), (3, "shoulder"), (5, "chest"), (6, "waist"), (7, "legs"), (8, "feet"),
        (9, "wrist"), (10, "hands"), (15, "back"), (16, "mainhand"), (17, "offhand"),
    ];

    private const int Head = 1, Back = 15;
    private const string Hidden = "h", Empty = "0";

    // What a capture's record says it showed: the equipped look, and the drawn one in its outfit.
    private sealed record Captured(string? Look, string? DrawnLook, long? Outfit);

    private readonly IReadOnlyDictionary<string, RosterCharacter> _roster;
    private readonly IReadOnlyDictionary<(string Guid, long Epoch), Captured> _looks;

    private PortraitJudge(IReadOnlyDictionary<string, RosterCharacter> roster, IReadOnlyDictionary<(string Guid, long Epoch), Captured> looks)
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
        // Each capture's looks, by its shot-1 epoch: what a converted capture's sidecar keeps.
        var looks = new Dictionary<(string, long), Captured>();
        foreach (var record in snapshot.Stores.SelectMany(s => s.Renders))
        {
            if (record is not { Shot: 1, Epoch: { } epoch }) continue;
            var look = record.Look is { Length: > 0 } l ? l : null;
            var drawn = record.DrawnLook is { Length: > 0 } d ? d : null;
            if (look is not null || drawn is not null) looks.TryAdd((record.Guid, epoch), new Captured(look, drawn, record.Outfit));
        }
        return new PortraitJudge(roster, looks);
    }

    /// <summary>
    /// The worn gear in a capture's <c>look</c> form, <c>slot:value;...</c>: the item id, <c>0</c>
    /// for an empty slot, <c>h</c> for a helm or cloak equipped while <c>hidehelm</c> /
    /// <c>hidecloak</c> is 1. Null when the record has no gear at all.
    /// </summary>
    public static string? GearOf(Func<string, double?> number) => Gear(number, drawn: false);

    /// <summary>
    /// The gear as the game draws it, in a capture's <c>drawnLook</c> form (AltStable#497): a slot's
    /// <c>gearlook_&lt;key&gt;</c> where there is one (the transmog's item, <c>0</c> for a hidden
    /// appearance, written <c>h</c>), else the slot as <see cref="GearOf"/> has it. Null when the
    /// record has no gear at all.
    /// </summary>
    public static string? DrawnGearOf(Func<string, double?> number) => Gear(number, drawn: true);

    private static string? Gear(Func<string, double?> number, bool drawn)
    {
        var hideHelm = number("hidehelm") == 1;
        var hideCloak = number("hidecloak") == 1;
        var parts = new List<string>();
        foreach (var (slot, key) in Slots)
        {
            string value;
            if (drawn && Item(number("gearlook_" + key)) is { } shown)
            {
                value = shown == 0 ? Hidden : shown.ToString(CultureInfo.InvariantCulture);
            }
            else if (Item(number("gearid_" + key)) is { } item)
            {
                value = item == 0 ? Empty
                    : (slot == Head && hideHelm) || (slot == Back && hideCloak) ? Hidden
                    : item.ToString(CultureInfo.InvariantCulture);
            }
            else continue;
            parts.Add(slot.ToString(CultureInfo.InvariantCulture) + ":" + value);
        }
        return parts.Count == 0 ? null : string.Join(";", parts);
    }

    private static long? Item(double? id) => id is double v && v >= 0 && v <= long.MaxValue ? (long)v : null;

    /// <summary>Whether the render, not the capture of <paramref name="captureEpoch"/>, is the character's portrait.</summary>
    public bool RenderWins(string guid, long? captureEpoch, CutoutMeta render)
    {
        if (Armory.AsOf(render) is null) return false;
        if (captureEpoch is not { } epoch) return true;
        // Case 4 and case 5 ask the same of the render, newer than the capture; only a
        // character known unchanged since the capture keeps it whatever the render.
        return ChangedSince(guid, epoch) != false && Armory.RenderWins(epoch, render);
    }

    // Whether the character changed since the capture; null when it cannot be told (case 5).
    private bool? ChangedSince(string guid, long epoch)
    {
        if (!_looks.TryGetValue((guid, epoch), out var capture) || !_roster.TryGetValue(guid, out var c)) return null;
        bool? gear;
        if (capture.DrawnLook is { } drawn)
        {
            // A capture drawn from the posed stage before the roster's first drawn reading: the
            // equipped ids would miss a transmog, so no verdict (Codex on #95).
            var at = ArmoryEligibility.Seconds(c.DrawnLookAt);
            if (c.DrawnGear is not { } drawnNow || at == 0) return null;
            // Another saved outfit than the capture's: no verdict about the gear.
            if (capture.Outfit is { } then && c.DrawnOutfit is { } now && then != now) return null;
            gear = DrawnChanged(drawn, drawnNow, at, epoch);
        }
        else if (capture.Look is { } look && c.Gear is { } worn)
        {
            gear = WornChanged(look, worn, c, epoch);
        }
        else return null;
        if (gear is not { } changed) return null;
        return changed || ArmoryEligibility.Seconds(c.LookAt) > epoch;
    }

    // The drawn look, AltStable#497: any slot drawn otherwise is a change of the look, whether a
    // transmog, a hidden appearance or an item drawn as equipped, all dated by drawnLookAt. An
    // equipped swap under an unchanged transmog is no change: the portrait looks the same.
    private static bool DrawnChanged(string drawnLook, string drawnNow, long at, long epoch)
    {
        var captured = Parse(drawnLook);
        var now = Parse(drawnNow);
        var differs = Slots.Any(s => captured.TryGetValue(s.Slot, out var a) && now.TryGetValue(s.Slot, out var b) && a != b);
        // Seen at or before the capture: the capture read the look itself.
        return differs && at > epoch;
    }

    // The equipped look, for a capture or a roster from before the drawn one.
    private static bool? WornChanged(string look, string gear, RosterCharacter c, long epoch)
    {
        var captured = Parse(look);
        var worn = Parse(gear);
        var changed = false;
        foreach (var (slot, _) in Slots)
        {
            if (!captured.TryGetValue(slot, out var then) || !worn.TryGetValue(slot, out var now) || then == now) continue;
            // An item on both sides, hidden on one: the same item, shown or not.
            var shownOnly = (then == Hidden && now != Empty) || (now == Hidden && then != Empty);
            var at = shownOnly ? First(c.GearReadAt, c.Updated) : First(c.GearAt, c.Updated);
            if (at == 0) return null;
            // Seen at or before the capture: the roster's record is older than the capture,
            // which read what was worn itself. Not a change since it.
            if (at > epoch) changed = true;
        }
        return changed;
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
