using System;
using ExileCore2;
using ExileCore2.PoEMemory;

namespace ExileStats;

/// <summary>
/// Reads the live in-map ritual tribute total. Tribute is <b>not</b> a player/altar stat - confirmed
/// live 2026-09-06: the altar's Stats hold only VirtualOnFullLife, its StateMachine only
/// current_state / rituals_completed, and nothing ritual-shaped exists on Player.Stats or ServerData.
/// The IngameUi.RitualWindow copy is stale during combat (it isn't even visible).
///
/// Primary path is a <b>direct pointer chain</b>, no UI involved at all - see the offsets below. It was
/// lifted straight out of the client's kill-points widget update (0x140513850), which computes the
/// number it draws as <c>[[widget+0x2D0] + 0x8550] + 0x18</c>; widget+0x2D0 is the same context object
/// ServerData holds at +0x60, so the widget drops out of the chain entirely. IngameUi+0x310 reaches it
/// too, but that sits in a run of UI element pointers that shifts whenever a window is added, while
/// +0x60 is early in a struct whose head rarely moves.
///
/// Fallback is the old HUD scrape: RitualWindow is a named property on IngameUi and the kill-points
/// widget is a sibling under RitualWindow.Parent, shaped as exactly 3 children (background image,
/// numeric text, icon overlay) with the digits in child [1]. It only runs if the chain doesn't resolve,
/// which is what a game patch moving the offsets looks like. Never <c>FindChildRecursive</c> - that
/// walks the whole UI root and measured ~600ms on a live ritual, stalling the render loop.
///
/// Returns null when no ritual is running. Never throws.
/// </summary>
public static class RitualTribute
{
    // Pointer chain, PoE2 0.5.5. Verified live: chain and HUD both read 187 in the same frame.
    private const int ServerToContext = 0x60;   // ServerData -> encounter context (IngameUi+0x310 also reaches it)
    private const int ContextToRitual = 0x8550; // context -> live ritual state, 0 when no ritual
    private const int RitualToTribute = 0x18;   // ritual state -> tribute count (int)
    private const int MaxSaneTribute = 1_000_000;

    private const int WidgetChildCount = 3;     // background, numeric text, icon overlay
    private const int TextChild = 1;

    private static int _index = -1;             // last known child index of the widget under RitualWindow.Parent

    public static int? Read(GameController gc)
    {
        if (gc == null)
            return null;
        return ReadDirect(gc) ?? ReadHud(gc);
    }

    // Three reads, no UI walk, no digit parse.
    private static int? ReadDirect(GameController gc)
    {
        try
        {
            var sd = gc.IngameState?.ServerData?.Address ?? 0;
            if (sd == 0)
                return null;

            var mem = gc.Memory;
            var ctx = mem.Read<long>(sd + ServerToContext);
            if (ctx == 0)
                return null;

            var ritual = mem.Read<long>(ctx + ContextToRitual);
            if (ritual == 0)
                return null;                    // no ritual running

            var v = mem.Read<int>(ritual + RitualToTribute);
            // a patch moving the offsets reads garbage here, and garbage tribute is worse than none -
            // bail so ReadHud takes over instead of logging it
            return v >= 0 && v <= MaxSaneTribute ? v : (int?)null;
        }
        catch
        {
            return null;
        }
    }

    // Kept for when a patch moves the offsets above. Scrapes the digits off the HUD widget.
    private static int? ReadHud(GameController gc)
    {
        try
        {
            var parent = gc.IngameState?.IngameUi?.RitualWindow?.Parent;
            var kids = parent?.Children;
            if (kids == null || kids.Count == 0)
                return null;

            // remembered slot first
            if (_index >= 0 && _index < kids.Count)
            {
                var v = ValueOf(kids[_index]);
                if (v.HasValue)
                    return v;
            }

            // one level of siblings, not the whole tree
            for (var i = 0; i < kids.Count; i++)
            {
                var v = ValueOf(kids[i]);
                if (!v.HasValue)
                    continue;
                _index = i;
                return v;
            }

            return null;
        }
        catch
        {
            _index = -1;
            return null;
        }
    }

    // The widget shape: visible, exactly 3 children, middle child holds the digits. Null if it isn't ours.
    private static int? ValueOf(Element e)
    {
        if (e == null || !e.IsValid || !e.IsVisible)
            return null;

        var kids = e.Children;
        if (kids == null || kids.Count != WidgetChildCount)
            return null;

        return Digits(kids[TextChild]?.Text);
    }

    // "1,127" -> 1127. Null when there is no digit at all, so an empty or non-numeric label can't read as 0.
    private static int? Digits(string s)
    {
        if (string.IsNullOrEmpty(s))
            return null;
        var v = 0;
        var any = false;
        foreach (var ch in s)
        {
            if (ch < '0' || ch > '9')
                continue;
            v = v * 10 + (ch - '0');
            any = true;
        }
        return any ? v : (int?)null;
    }
}
