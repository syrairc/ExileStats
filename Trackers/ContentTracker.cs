using System;
using System.Collections.Generic;
using ExileCore2.PoEMemory.MemoryObjects;
using ExileCore2.Shared.Enums;

namespace ExileStats;

/// <summary>
/// Per-Tick scan of <c>GameController.Entities</c> for map content (ritual / breach / strongbox / essence /
/// boss / …). Records WHERE (<c>GridPos</c>) + WHEN (first-seen elapsed) each content instance appears, and
/// upgrades its terminal state (opened / used / dead) in place on later ticks. No UI.
///
/// Dedup is by <b>Entity.Id</b> (unique + per-area-stable) so an instance is logged exactly once at its
/// <i>first</i> observed position — content that moves (map bosses, rogue exiles, tormented spirits) must not
/// drop a new point every tick. A position fingerprint (type + rounded grid pos) is the cross-visit key:
/// seeded from content.json on entry so re-entry / restart don't re-log, and it collapses co-located
/// entities of one mechanic (e.g. a ritual's rune object + interactable) into a single row.
/// </summary>
public class ContentTracker
{
    // Entity.Id -> the logged sighting, for everything seen this session. Keeps a moving entity pinned to its
    // first row (no per-tick re-log) while still allowing state upgrades.
    private readonly Dictionary<long, ContentSighting> _byId = new();

    // Fingerprint (type@roundedPos) -> sighting. Seeded from disk on entry; the cross-visit / co-located key.
    private readonly Dictionary<string, ContentSighting> _known = new();
    private int _zoneSwitchId;

    // Ritual tribute: the site whose altar is currently active (one fights at a time) + the last HUD tribute
    // total read, so positive deltas can be attributed to that site. Cleared when no ritual is active.
    private string _activeRitualFp;
    private int _lastTribute;

    /// <summary>Reset + seed from already-logged sightings for the instance we're entering;
    /// <paramref name="zoneSwitchId"/> tags ones first seen this visit.</summary>
    public void SetArea(IDictionary<string, ContentSighting> known, int zoneSwitchId)
    {
        _byId.Clear();
        _known.Clear();
        _zoneSwitchId = zoneSwitchId;
        if (known != null)
            foreach (var kv in known)
                _known[kv.Key] = kv.Value;
    }

    /// <summary>Scan the entity list once; return sightings that are new or whose terminal state changed
    /// (the caller upserts them to content.json). <paramref name="readTribute"/> reads the live ritual-tribute
    /// HUD total; it's invoked only while a ritual is active (so the UI walk is skipped otherwise).
    /// <paramref name="readExpedition"/> reads Expedition runestone rune count + reward pool/offer; it's invoked
    /// only while a runestone is present this tick (so the label/window walk is skipped otherwise).</summary>
    public List<ContentSighting> Scan(IEnumerable<Entity> entities, double elapsedSeconds,
        Func<int?> readTribute = null, Func<List<ExpeditionSiteInfo>> readExpedition = null,
        Func<RitualRewardState> readRitualRewards = null)
    {
        var dirty = new List<ContentSighting>();
        if (entities == null)
            return dirty;

        var elapsed = Math.Round(elapsedSeconds, 1);

        // Fingerprint of the ritual site whose altar is active this tick (current_state == 2), if any.
        string activeRitualFp = null;
        // Whether any Expedition runestone matched this tick (gates the label/window read below).
        var sawExpedition = false;

        foreach (var e in entities)
        {
            // Skip not-yet-streamed entities — components aren't safe to read; they're re-scanned each Tick
            // and logged once close enough to be valid (which is also "when" they appear to the player).
            if (e is not { IsValid: true })
                continue;

            var def = ContentCatalog.Classify(e);
            if (def == null)
                continue;

            // Note the active ritual site (any co-located RitualRune* shares one fingerprint). The matching
            // sighting is created/bound below in the normal flow, so we resolve it from _known after the loop.
            if (def.Type == "Ritual" && ContentCatalog.RitualState(e) == 2)
            {
                var rp = e.GridPos;
                activeRitualFp = ContentSighting.MakeFingerprint(def.Type, rp.X, rp.Y);
            }

            if (def.Type == "Expedition")
                sawExpedition = true;

            // Already tracking this exact entity: keep its first position, only upgrade terminal state.
            if (_byId.TryGetValue(e.Id, out var byId))
            {
                UpdateState(byId, def, e, elapsed, dirty);
                continue;
            }

            // New entity this session. If a sighting already sits at this spot (a prior visit, or a
            // co-located sibling entity of the same mechanic), bind to it instead of adding a new row.
            var pos = e.GridPos;
            var fp = ContentSighting.MakeFingerprint(def.Type, pos.X, pos.Y);
            if (_known.TryGetValue(fp, out var prior))
            {
                _byId[e.Id] = prior;
                UpdateState(prior, def, e, elapsed, dirty);
                continue;
            }

            // Genuinely new content instance — log it at this first-seen position.
            var s = new ContentSighting
            {
                Type = def.Type,
                Path = e.Path,
                Icon = (int)def.Icon,
                GridX = pos.X,
                GridY = pos.Y,
                FirstSeenAt = DateTime.Now,
                ElapsedSeconds = elapsed,
                Completed = def.Completed(e),
                Rarity = e.Type == EntityType.Monster ? SafeRarity(e) : null,
                ZoneSwitchId = _zoneSwitchId,
                Fingerprint = fp,
            };
            if (s.Completed == true)
                s.CompletedSeconds = elapsed;

            _known[fp] = s;
            _byId[e.Id] = s;
            dirty.Add(s);
        }

        AccumulateRitualTribute(activeRitualFp, readTribute);

        if (sawExpedition)
            ApplyExpedition(readExpedition, dirty);

        ApplyRitualRewards(readRitualRewards, dirty);

        return dirty;
    }

    // Replicate the map-wide ritual favour union + reroll count onto every ritual sighting (favours are a
    // shared tribute pool, not per-site). The reader returns non-null only when something changed (a favour
    // offered/purchased or a reroll), so the touched sightings are queued for the disk upsert.
    private void ApplyRitualRewards(Func<RitualRewardState> readRitualRewards, List<ContentSighting> dirty)
    {
        var state = readRitualRewards?.Invoke();
        if (state == null)
            return;

        foreach (var s in _known.Values)
        {
            if (s.Type != "Ritual")
                continue;
            s.RitualFavours = state.Favours;
            s.RitualRerolls = state.Rerolls;
            if (!dirty.Contains(s))
                dirty.Add(s);
        }
    }

    // Upsert Expedition rune count + reward pool/offer onto the matching runestone sighting (by fingerprint).
    // Each field is captured once (or when its option/pool count changes) so content.json isn't rewritten every
    // tick; a changed sighting is queued for the disk upsert.
    private void ApplyExpedition(Func<List<ExpeditionSiteInfo>> readExpedition, List<ContentSighting> dirty)
    {
        var sites = readExpedition?.Invoke();
        if (sites == null)
            return;

        foreach (var site in sites)
        {
            var fp = ContentSighting.MakeFingerprint("Expedition", site.GridX, site.GridY);
            if (!_known.TryGetValue(fp, out var s))
                continue;

            var changed = false;

            if (site.RuneCount.HasValue && s.RuneCount != site.RuneCount)
            {
                s.RuneCount = site.RuneCount;
                changed = true;
            }

            // Capture the computed pool once, or refresh if its size changed (e.g. priced after a late pricer load).
            if (site.Pool is { Count: > 0 } && (s.RewardPool == null || s.RewardPool.Count != site.Pool.Count))
            {
                s.RewardPool = site.Pool;
                changed = true;
            }

            // Capture the actual offered options once the window has been opened.
            if (site.Offered is { Count: > 0 } && s.OfferedRewards == null)
            {
                s.OfferedRewards = site.Offered;
                changed = true;
            }

            if (changed && !dirty.Contains(s))
                dirty.Add(s);
        }
    }

    // Attribute the live HUD tribute's positive deltas to the active ritual site. Tribute is map-wide and
    // accumulates as wave mobs die (it also drops when spent at the reward window — ignored here, we only add
    // positive deltas). The running total mutates the sighting in memory; it's flushed to disk by the
    // completion edge (current_state -> 3) via UpdateState, so we don't write content.json every tick.
    private void AccumulateRitualTribute(string activeFp, Func<int?> readTribute)
    {
        if (activeFp == null || !_known.TryGetValue(activeFp, out var rit))
        {
            _activeRitualFp = null;
            return;
        }

        var tribute = readTribute?.Invoke();
        if (!tribute.HasValue)
            return;

        if (activeFp != _activeRitualFp)
        {
            // First tick this site is active: baseline the counter (tribute may already be >0 from earlier sites).
            _activeRitualFp = activeFp;
            _lastTribute = tribute.Value;
            rit.TributeGained ??= 0;
            return;
        }

        var delta = tribute.Value - _lastTribute;
        if (delta > 0)
            rit.TributeGained = (rit.TributeGained ?? 0) + delta;
        _lastTribute = tribute.Value;
    }

    // Upgrade a tracked sighting's terminal state if it progressed; queue it for the disk upsert.
    private static void UpdateState(ContentSighting s, ContentDef def, Entity e, double elapsed,
        List<ContentSighting> dirty)
    {
        var done = def.Completed(e);
        if (done.HasValue && s.Completed != done)
        {
            s.Completed = done;
            if (done == true && s.CompletedSeconds == null)
                s.CompletedSeconds = elapsed;
            dirty.Add(s);
        }
    }

    private static string SafeRarity(Entity e)
    {
        try { return e.Rarity.ToString(); } catch { return null; }
    }
}
