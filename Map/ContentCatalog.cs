using System;
using System.Collections.Generic;
using System.Drawing;
using ExileCore2.PoEMemory.Components;
using ExileCore2.PoEMemory.MemoryObjects;
using ExileCore2.Shared.Enums;

namespace ExileStats;

/// <summary>
/// One content type's detection rule + icon. Maps an <see cref="Entity"/> (path / type / component
/// discriminator) to a canonical <see cref="Type"/> name, a <see cref="MapIconsIndex"/> sprite in the shared
/// Icons.png sheet, and a <see cref="Tint"/> that disambiguates types sharing a generic sprite. Every
/// component read is wrapped, so a torn / not-yet-streamed entity never throws.
/// </summary>
public sealed class ContentDef
{
    public string Type { get; }
    public MapIconsIndex Icon { get; }
    public Color Tint { get; }
    // Can this rule ever match an EntityType.Monster entity? false for the interactables (chest / shrine /
    // terrain objects — never monsters), so Classify skips them for the (large) monster bucket. Only the
    // monster-content rules (boss / rogue exile / tormented spirit) keep the default true.
    public bool CanMatchMonster { get; }
    private readonly Func<Entity, bool> _match;
    private readonly Func<Entity, bool?> _completed;

    public ContentDef(string type, MapIconsIndex icon, Color tint,
        Func<Entity, bool> match, Func<Entity, bool?> completed = null, bool canMatchMonster = true)
    {
        Type = type;
        Icon = icon;
        Tint = tint;
        CanMatchMonster = canMatchMonster;
        _match = match;
        _completed = completed;
    }

    public bool Matches(Entity e)
    {
        try { return _match(e); } catch { return false; }
    }

    /// <summary>Terminal state (opened / used / dead), or null when unknown / not applicable.</summary>
    public bool? Completed(Entity e)
    {
        if (_completed == null) return null;
        try { return _completed(e); } catch { return null; }
    }
}

/// <summary>
/// Single source of truth for map-content detection + iconography. <see cref="Classify"/> returns the first
/// matching <see cref="ContentDef"/> for an entity (order is specific → general). Discriminators come from the
/// exilecore2-memory object-tree specs and the MinimapIcons plugin's path checks.
///
/// Icon choices use <see cref="MapIconsIndex"/> values confirmed present in ExileCore2.dll (Icons.png).
/// Several content types have no dedicated game sprite, so they share a generic one (QuestObject) and are
/// told apart by <see cref="ContentDef.Tint"/>. To refine, browse the full enum
/// (<c>Enum.GetValues&lt;MapIconsIndex&gt;()</c> — the same list the MinimapIcons icon picker shows) and swap
/// in a better-matching value below.
/// </summary>
public static class ContentCatalog
{
    private static bool PathHas(Entity e, string s) => e?.Path?.Contains(s) == true;
    private static bool PathEnds(Entity e, string s) => e?.Path?.EndsWith(s, StringComparison.Ordinal) == true;

    private static bool HasBossMod(Entity e)
    {
        var mods = e.GetComponent<ObjectMagicProperties>()?.Mods;
        if (mods == null) return false;
        foreach (var m in mods)
            if (m != null && m.Contains("Boss")) return true;
        return false;
    }

    private static bool? ShrineUsed(Entity e)
    {
        var s = e.GetComponent<Shrine>();
        return s == null ? (bool?)null : !s.IsAvailable;
    }

    /// <summary>The ritual altar's <c>current_state</c> phase (1 = available, 2 = active fight,
    /// 3 = completed), or null if unreadable. Read off any RitualRune* StateMachine.</summary>
    public static int? RitualState(Entity e)
    {
        var states = e.GetComponent<StateMachine>()?.States;
        if (states == null) return null;
        foreach (var s in states)
            if (s.Name == "current_state")
                return (int)s.Value;
        return null;
    }

    /// <summary>A named <c>StateMachine</c> state value off an entity, or null if unreadable. Used for
    /// Expedition runestones: <c>"sockets"</c> = rune count, <c>"activated"</c> (6 = triggered/rewards
    /// available). Mirrors the reads in the Expedition2Good plugin.</summary>
    public static int? StateValue(Entity e, string name)
    {
        var states = e.GetComponent<StateMachine>()?.States;
        if (states == null) return null;
        foreach (var s in states)
            if (s.Name == name)
                return (int)s.Value;
        return null;
    }

    // First match wins — keep specific discriminators above broad Type==Monster ones.
    public static readonly IReadOnlyList<ContentDef> All = new List<ContentDef>
    {
        new("Strongbox", MapIconsIndex.Strongbox, Color.White,
            e => PathHas(e, "Metadata/Chests/StrongBoxes"),
            e => e.IsOpened, canMatchMonster: false),
        new("Shrine", MapIconsIndex.Shrine, Color.White,
            e => e.HasComponent<Shrine>(),
            ShrineUsed, canMatchMonster: false),
        new("Ritual", MapIconsIndex.RitualRune, Color.White,
            e => PathHas(e, "Terrain/Leagues/Ritual/RitualRune"),
            e => RitualState(e) is int s ? s >= 3 : (bool?)null, canMatchMonster: false),
        new("Breach", MapIconsIndex.Breach, Color.White,
            e => PathHas(e, "Brequel/BrequelInitiator"), canMatchMonster: false),
        new("Essence", MapIconsIndex.Essence, Color.White,
            e => e.Path == "Metadata/MiscellaneousObjects/Monolith", canMatchMonster: false),
        new("Expedition", MapIconsIndex.ExpeditionChest2, Color.White,
            e => PathHas(e, "Expedition2/Expedition2Encounter"),
            e => StateValue(e, "activated") is int v ? v == 6 : (bool?)null, canMatchMonster: false),
        new("Abyss", MapIconsIndex.AbyssPitActive, Color.White,
            e => PathEnds(e, "AbyssFinalNodeBase"), canMatchMonster: false),
        new("Abyss Crack", MapIconsIndex.AbyssCrack, Color.White,
            e => PathEnds(e, "AbyssCrack"), canMatchMonster: false),
        new("Delirium", MapIconsIndex.DeliriumMirror, Color.White,
            e => PathHas(e, "DeliriumInitiator"), canMatchMonster: false),
        new("Incursion", MapIconsIndex.IncursionArchitectReplace, Color.White,
            e => PathHas(e, "IncursionPedestalEncounter"), canMatchMonster: false),
        new("Checkpoint", MapIconsIndex.Checkpoint, Color.White,
            e => PathHas(e, "Checkpoints/Checkpoint"), canMatchMonster: false),
        new("Tormented Spirit", MapIconsIndex.SpiritActivated, Color.White,
            e => PathHas(e, "/TormentedSpirits/") && !PathHas(e, "Possesed")),
        new("Summoning Stone", MapIconsIndex.StoneCircle, Color.White,
            e => PathEnds(e, "RuneRock"), canMatchMonster: false),
        new("Rogue Exile", MapIconsIndex.RogueExile, Color.White,
            e => e.Type == EntityType.Monster && PathHas(e, "/RogueExiles/"),
            e => !e.IsAlive),
        new("Map Boss", MapIconsIndex.UniqueMonsterAlive, Color.White,
            e => e.Type == EntityType.Monster && e.Rarity == MonsterRarity.Unique
                 && (PathHas(e, "BossMAP") || HasBossMod(e)),
            e => !e.IsAlive),
    };

    /// <summary>The matching content def for an entity, or null when it isn't tracked content. For a monster
    /// entity (the bulk of the valid set), the interactable rules are skipped — they never match a monster —
    /// so only the 3 monster-content rules are evaluated.</summary>
    public static ContentDef Classify(Entity e)
    {
        if (e == null || string.IsNullOrEmpty(e.Path)) return null;
        var isMonster = false;
        try { isMonster = e.Type == EntityType.Monster; } catch { /* torn entity */ }
        foreach (var d in All)
        {
            if (isMonster && !d.CanMatchMonster) continue;
            if (d.Matches(e)) return d;
        }
        return null;
    }

    private static readonly Dictionary<string, ContentDef> ByType = BuildByType();

    private static Dictionary<string, ContentDef> BuildByType()
    {
        var m = new Dictionary<string, ContentDef>();
        foreach (var d in All) m[d.Type] = d;
        return m;
    }

    /// <summary>The def for a canonical type name, or null if unknown.</summary>
    public static ContentDef ForType(string type) =>
        type != null && ByType.TryGetValue(type, out var d) ? d : null;

    /// <summary>"#RRGGBB" tint for a content type (gray for unknown types).</summary>
    public static string HexFor(string type) => ToHex(ForType(type)?.Tint ?? Color.Gray);

    public static string ToHex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";
}
