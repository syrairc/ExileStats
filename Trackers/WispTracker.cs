using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using ExileCore2.PoEMemory.Components;
using ExileCore2.PoEMemory.MemoryObjects;
using ExileCore2.Shared.Enums;
using Vector2 = System.Numerics.Vector2;

namespace ExileStats;

/// <summary>
/// Per-Tick scan of the shared <c>AllValid</c> + <c>Monsters</c> buckets for Tormented Spirit / Azmeri wisp
/// encounters. A wisp
/// roams the map touching monsters (applying a buff) and eventually possesses a Rare/Unique. An encounter
/// <b>starts</b> when the first monster gains the wisp buff and <b>ends</b> when a rare becomes possessed;
/// the resolved encounter is returned as one <see cref="WispEncounter"/> (buffed/slain counts + the victim's
/// initial location). No UI.
///
/// Why a proxy count: the true reward (monsters slain before possession) is kept server-side and is not
/// client-readable (verified — see the exilecore2-memory TormentedSpirit spec). We count buffed monsters
/// observed dead before possession instead.
/// </summary>
public class WispTracker
{
    /// <summary>Monster buff names that mean "touched by a wisp". <c>unholy_might</c> is the confirmed Ox
    /// touch buff; the <b>common</b> wisp buff (shared across Ox / Owl / Bear) must be sampled from a live
    /// wisp and added here — until then detection is best-effort (the Owl roaming wisp carried no buffs when
    /// sampled, so it won't register on unholy_might alone).</summary>
    // TODO: add the common wisp buff id once sampled live — unholy_might is Ox-specific; Owl/Bear use a different buff.
    public static readonly HashSet<string> WispBuffNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "unholy_might",
    };

    // One in-progress encounter, keyed in _active by the wisp's Entity.Id.
    private sealed class ActiveEncounter
    {
        public string Variant;
        public float WispGridX, WispGridY;
        public double StartedSeconds;
        public readonly HashSet<long> BuffedIds = new();  // distinct monsters touched
        public readonly HashSet<long> SlainIds = new();   // of those, counted dead (once each)
    }

    private int _zoneSwitchId;
    private readonly Dictionary<string, WispEncounter> _known = new();   // fingerprint -> resolved (cross-visit)
    private readonly Dictionary<long, ActiveEncounter> _active = new();  // wisp Entity.Id -> in-progress
    private readonly Dictionary<long, Vector2> _rareFirstPos = new();    // rare/unique id -> first-seen pos
    private readonly HashSet<long> _resolvedVictims = new();             // possessed ids already turned into a record

    /// <summary>Reset all in-memory state + seed the cross-visit dedup set from disk;
    /// <paramref name="zoneSwitchId"/> tags encounters resolved this visit.</summary>
    public void SetArea(IDictionary<string, WispEncounter> known, int zoneSwitchId)
    {
        _zoneSwitchId = zoneSwitchId;
        _known.Clear();
        _active.Clear();
        _rareFirstPos.Clear();
        _resolvedVictims.Clear();
        if (known != null)
            foreach (var kv in known)
                _known[kv.Key] = kv.Value;
    }

    /// <summary>Scan once; return encounters resolved (possessed) this tick. <paramref name="allValid"/> is only
    /// walked for the wisp entity itself (type unconfirmed); monster work uses the shared Monster bucket.</summary>
    public List<WispEncounter> Scan(IReadOnlyList<Entity> allValid, IReadOnlyList<Entity> monsters,
        double elapsedSeconds)
    {
        var dirty = new List<WispEncounter>();
        if (allValid == null || monsters == null)
            return dirty;

        var elapsed = Math.Round(elapsedSeconds, 1);

        // active wisps only. cheap Contains reject inside IsActiveWisp, regex only on a hit
        var wisps = new List<(long Id, Vector2 Pos, string Variant)>();
        foreach (var e in allValid)
        {
            if (e is not { IsValid: true })
                continue;
            if (IsActiveWisp(e))
                wisps.Add((e.Id, e.GridPos, VariantOf(e.Path)));
        }

        // 1. While a wisp roams, remember each rare/unique's first-seen position. We only learn which rare is
        //    the victim at possession, so this is how we recover its *initial* location.
        if (wisps.Count > 0)
            foreach (var e in monsters)
                if (IsRareOrUnique(e) && !_rareFirstPos.ContainsKey(e.Id))
                    _rareFirstPos[e.Id] = e.GridPos;

        // 2. Buffed monsters -> attribute to the nearest active wisp; start that encounter on its first buff.
        if (wisps.Count > 0)
            foreach (var e in monsters)
            {
                if (!HasWispBuff(e))
                    continue;
                var enc = GetOrStart(Nearest(wisps, e.GridPos), elapsed);
                enc.BuffedIds.Add(e.Id);
            }

        // 3. Slain detection: a tracked buffed monster observed dead before possession (counted once).
        if (_active.Count > 0)
        {
            var aliveById = new Dictionary<long, bool>();
            foreach (var e in monsters)
                aliveById[e.Id] = IsAlive(e);
            foreach (var enc in _active.Values)
                foreach (var id in enc.BuffedIds)
                    if (!enc.SlainIds.Contains(id) && aliveById.TryGetValue(id, out var alive) && !alive)
                        enc.SlainIds.Add(id);
        }

        // 4. Possession ends an encounter. Resolve the nearest active encounter into a record. (At possession
        //    the wisp entity is gone, so the encounter lives only in _active — match by wisp->victim distance.)
        foreach (var e in monsters)
        {
            // rarity first: only rares/uniques get possessed, and the Stats dictionary read is the expensive part
            if (!IsRareOrUnique(e) || _resolvedVictims.Contains(e.Id) || !IsPossessed(e))
                continue;
            _resolvedVictims.Add(e.Id);
            if (_active.Count == 0)
                continue;   // never saw a buff phase (e.g. immediate possession) — nothing to attribute

            var victimPos = _rareFirstPos.TryGetValue(e.Id, out var fp) ? fp : e.GridPos;
            if (NearestEncounterKey(victimPos) is not { } wid)
                continue;
            var enc = _active[wid];
            _active.Remove(wid);

            var rec = new WispEncounter
            {
                Variant = enc.Variant,
                WispGridX = enc.WispGridX,
                WispGridY = enc.WispGridY,
                VictimGridX = victimPos.X,
                VictimGridY = victimPos.Y,
                BuffedCount = enc.BuffedIds.Count,
                SlainBuffedCount = enc.SlainIds.Count,
                StartedSeconds = enc.StartedSeconds,
                PossessedSeconds = elapsed,
                DoublePossessed = IsDoublePossessed(e),
                ZoneSwitchId = _zoneSwitchId,
                Fingerprint = WispEncounter.MakeFingerprint(victimPos.X, victimPos.Y),
            };
            _known[rec.Fingerprint] = rec;
            dirty.Add(rec);
        }

        return dirty;
    }

    private ActiveEncounter GetOrStart((long Id, Vector2 Pos, string Variant) wisp, double elapsed)
    {
        if (_active.TryGetValue(wisp.Id, out var enc))
            return enc;
        enc = new ActiveEncounter
        {
            Variant = wisp.Variant,
            WispGridX = wisp.Pos.X,
            WispGridY = wisp.Pos.Y,
            StartedSeconds = elapsed,
        };
        _active[wisp.Id] = enc;
        return enc;
    }

    private long? NearestEncounterKey(Vector2 p)
    {
        long? best = null;
        var bestD = float.MaxValue;
        foreach (var kv in _active)
        {
            var d = Vector2.DistanceSquared(new Vector2(kv.Value.WispGridX, kv.Value.WispGridY), p);
            if (d < bestD) { bestD = d; best = kv.Key; }
        }
        return best;
    }

    private static (long Id, Vector2 Pos, string Variant) Nearest(
        List<(long Id, Vector2 Pos, string Variant)> wisps, Vector2 p)
    {
        var best = wisps[0];
        var bestD = Vector2.DistanceSquared(best.Pos, p);
        for (var i = 1; i < wisps.Count; i++)
        {
            var d = Vector2.DistanceSquared(wisps[i].Pos, p);
            if (d < bestD) { bestD = d; best = wisps[i]; }
        }
        return best;
    }

    // ---- entity reads (all wrapped: torn / not-yet-streamed entities never throw) ----

    // The roaming wisp entity is named TormentedSpiritofthe<Animal><Suffix>, suffix one of Wild / Primal /
    // Vivid / Sacred (confirmed on disk: Ox/Bear=Wild, Owl/Serpent=Primal, Cat=Vivid, Fox/Rabbit=Sacred). The
    // animal is captured as the variant. This excludes the noise that also lives under /TormentedSpirits/:
    // OxWarningMarker, BearProxySlam, OwlOnDeath, PrimateChieftain, the bare Spiritofthe<Animal> summon, and
    // (via the Possesed guard) the post-possession daemon.
    private static readonly Regex WispRe = new(
        @"ofthe([A-Za-z]+?)(Wild|Primal|Vivid|Sacred)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static bool IsActiveWisp(Entity e)
    {
        try { return WispVariant(e.Path) != null; }
        catch { return false; }
    }

    // Variant (animal) if the path is a roaming wisp, else null.
    private static string WispVariant(string path)
    {
        if (string.IsNullOrEmpty(path) || !path.Contains("/TormentedSpirits/") || path.Contains("Possesed"))
            return null;
        var m = WispRe.Match(path);
        return m.Success ? m.Groups[1].Value : null;
    }

    private static string VariantOf(string path) => WispVariant(path) ?? "Unknown";

    private static bool IsRareOrUnique(Entity e)
    {
        try { return e.Rarity is MonsterRarity.Rare or MonsterRarity.Unique; }
        catch { return false; }
    }

    private static bool HasWispBuff(Entity e)
    {
        try
        {
            var list = e.GetComponent<Buffs>()?.BuffsList;
            if (list == null) return false;
            foreach (var b in list)
                if (b?.Name != null && WispBuffNames.Contains(b.Name))
                    return true;
            return false;
        }
        catch { return false; }
    }

    // Unknown -> treat as alive, so a torn read never false-counts a slain monster.
    private static bool IsAlive(Entity e)
    {
        try { return e.IsAlive; } catch { return true; }
    }

    private static bool IsPossessed(Entity e) => StatIsOne(e, GameStat.PossessedBySacredSpirit);
    private static bool IsDoublePossessed(Entity e) => StatIsOne(e, GameStat.PossessedByWildSpirit);

    private static bool StatIsOne(Entity e, GameStat stat)
    {
        try
        {
            var dict = e.GetComponent<Stats>()?.StatDictionary;
            return dict != null && dict.TryGetValue(stat, out var v) && v == 1;
        }
        catch { return false; }
    }
}
