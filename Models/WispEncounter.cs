using System;

namespace ExileStats;

/// <summary>
/// One Tormented Spirit / Azmeri wisp encounter, resolved at the moment the wisp possesses a Rare/Unique.
/// A wisp roams the map touching monsters (applying a buff), then possesses a rare; the possession reward
/// scales with the number of touched monsters <i>slain before possession</i> — a count the game keeps
/// server-side and never exposes client-readably (see the exilecore2-memory TormentedSpirit spec). We
/// therefore reconstruct a proxy: <see cref="BuffedCount"/> monsters got the wisp buff, of which
/// <see cref="SlainBuffedCount"/> were observed dead before possession.
///
/// Recorded at the possessed rare's <b>initial</b> location (<see cref="VictimGridX"/>/<see cref="VictimGridY"/>)
/// — distinct from the wisp's first position, which the content tracker already logs as a "Tormented Spirit"
/// sighting. Deduped per instance by <see cref="Fingerprint"/> (rounded victim position) so re-entering the
/// same instance doesn't re-log it. Appended to the instance's wisps.json and drawn as a marker + hover
/// tooltip in the Map Statistics window.
/// </summary>
public class WispEncounter
{
    // Spirit variant parsed from the wisp path ("Ox" / "Owl" / "Bear" / "Unknown"). Different variants grant
    // different boosts.
    public string Variant { get; set; }

    // The wisp's first-seen position (grid units; same space as path/loot/content and the map.svg viewBox).
    public float WispGridX { get; set; }
    public float WispGridY { get; set; }

    // The possessed rare/unique's initial (first-seen) position — where the marker + tooltip are drawn.
    public float VictimGridX { get; set; }
    public float VictimGridY { get; set; }

    // Distinct monsters that received the wisp buff during the encounter.
    public int BuffedCount { get; set; }
    // Of those, the ones observed dead before possession — the headline number (reward proxy).
    public int SlainBuffedCount { get; set; }

    // Elapsed seconds (since area entry) at the first buff and at possession.
    public double StartedSeconds { get; set; }
    public double PossessedSeconds { get; set; }

    // Two spirits possessed the same rare (PossessedByWildSpirit == 1 in addition to PossessedBySacredSpirit).
    public bool DoublePossessed { get; set; }

    // Which visit saw it.
    public int ZoneSwitchId { get; set; }

    // Dedup key.
    public string Fingerprint { get; set; }

    /// <summary>Position-stable dedup key: rounded victim grid position. The victim is unique per encounter,
    /// so its location keys the record across re-entry.</summary>
    public static string MakeFingerprint(float x, float y) =>
        $"wisp@{(int)Math.Round(x)}:{(int)Math.Round(y)}";
}
