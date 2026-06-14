using System;
using System.Collections.Generic;

namespace ExileStats;

/// <summary>
/// One piece of map content (ritual / breach / strongbox / essence / boss / …) observed during a map run,
/// recording WHERE it sits (<see cref="GridX"/>/<see cref="GridY"/>) and WHEN it first streamed in
/// (<see cref="ElapsedSeconds"/>). Deduped per instance by <see cref="Fingerprint"/> (type + rounded grid
/// position — content is static) so re-entering the same instance or restarting the game doesn't re-log it;
/// the terminal state (<see cref="Completed"/> / <see cref="CompletedSeconds"/>) is upgraded in place on
/// later ticks. Appended to the instance's content.json and drawn as a per-type icon at its position in the
/// Map Statistics window and the activity-report HTML.
/// </summary>
public class ContentSighting
{
    // What it is. Type is the canonical name from ContentCatalog; Icon is the (int)MapIconsIndex sprite to
    // draw from the shared Icons.png sheet.
    public string Type { get; set; }
    public string Path { get; set; }
    public int Icon { get; set; }

    // Where (grid units, same space as path/loot/deaths and the map.svg viewBox).
    public float GridX { get; set; }
    public float GridY { get; set; }

    // When it first appeared.
    public DateTime FirstSeenAt { get; set; }
    public double ElapsedSeconds { get; set; }

    // Terminal state (opened / used / dead). Null = not applicable or not yet known. CompletedSeconds is
    // the elapsed time it first became completed.
    public bool? Completed { get; set; }
    public double? CompletedSeconds { get; set; }

    // Optional extra (set for monster content). Which visit first saw it.
    public string Rarity { get; set; }
    public int ZoneSwitchId { get; set; }

    // Tribute earned during this ritual's active window (sum of positive HUD-tribute deltas). Ritual only;
    // null for every other content type and for legacy data.
    public int? TributeGained { get; set; }

    // Expedition only (null for every other content type + legacy data). Socket/rune count of the runestone;
    // the computed-possible reward pool (from runes + area level); the actual offered options once the
    // runestone activated (rewards become available after nearby monsters are cleared).
    public int? RuneCount { get; set; }
    public List<ExpeditionReward> RewardPool { get; set; }
    public List<ExpeditionReward> OfferedRewards { get; set; }

    // Ritual only (null otherwise + legacy data). Union of all favours offered in the reward window across
    // rerolls (each flagged Purchased), plus the reroll count. Favours are a map-wide tribute pool, so the
    // same list is replicated onto every ritual sighting in the instance.
    public List<RitualReward> RitualFavours { get; set; }
    public int? RitualRerolls { get; set; }

    // Dedup key.
    public string Fingerprint { get; set; }

    /// <summary>Position-stable dedup key: type + rounded grid position. Pure (unit-tested).</summary>
    public static string MakeFingerprint(string type, float x, float y) =>
        $"{type}@{(int)Math.Round(x)}:{(int)Math.Round(y)}";
}
