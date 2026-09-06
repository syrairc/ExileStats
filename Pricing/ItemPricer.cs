using System;
using System.Linq;
using ExileCore2;
using ExileCore2.PoEMemory.MemoryObjects;
using ExileCore2.PoEMemory.Models;

namespace ExileStats;

/// <summary>
/// Prices an item Entity via the NinjaPricer plugin's PluginBridge method <c>NinjaPrice.GetValue</c>
/// (registered in NinjaPricer.cs). That method returns <c>PriceData.MinChaosValue</c> and is
/// <b>stack-aware</b> — for a stacked currency entity it already multiplies by the stack size, so the
/// returned value is the worth of the whole stack on that entity.
///
/// The bridge method is absent when NinjaPricer isn't loaded or hasn't downloaded prices yet; we fetch it
/// per call (so a late-loading pricer starts working without a plugin reload) and return null when it's
/// unavailable. Never throws.
/// </summary>
public static class ItemPricer
{
    public static double? GetChaosValue(Entity itemEntity, GameController gc)
    {
        if (itemEntity == null)
            return null;
        try
        {
            var fn = gc.PluginBridge.GetMethod<Func<Entity, double>>("NinjaPrice.GetValue");
            if (fn == null)
                return null;
            var v = fn(itemEntity);
            return double.IsNaN(v) || double.IsInfinity(v) ? (double?)null : Math.Max(0, v);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// One <c>BaseItemType</c>'s exalted value via the <c>NinjaPrice.GetBaseItemTypeValue</c> bridge (per-unit,
    /// not stack-aware). Returns null when NinjaPricer is absent/unpriced or the value is non-positive/invalid.
    /// Never throws. Used for Expedition reward recipes (reward is a <c>BaseItemType</c>).
    /// </summary>
    public static double? GetBaseItemTypeValue(BaseItemType bit, GameController gc)
    {
        if (bit == null)
            return null;
        try
        {
            var fn = gc.PluginBridge.GetMethod<Func<BaseItemType, double>>("NinjaPrice.GetBaseItemTypeValue");
            if (fn == null)
                return null;
            var v = fn(bit);
            return double.IsNaN(v) || double.IsInfinity(v) || v <= 0 ? (double?)null : v;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Current Divine Orb price in exalted (= NinjaPricer's DivineToExaltedRate). That rate isn't bridged
    /// directly, so we price the Divine Orb <c>BaseItemType</c> via <see cref="GetBaseItemTypeValue"/>, which
    /// returns one orb's exalted value. Returns null when NinjaPricer is absent/unpriced. Never throws.
    /// </summary>
    public static double? GetDivineRate(GameController gc)
    {
        try
        {
            // The BaseItemTypes scan is a linear walk of thousands of entries and the answer never changes,
            // so cache it - callers here poll this per frame.
            _divineBase ??= gc.Files.BaseItemTypes.Contents.Values.FirstOrDefault(b => b.BaseName == "Divine Orb");
            return GetBaseItemTypeValue(_divineBase, gc);
        }
        catch
        {
            return null;
        }
    }

    private static BaseItemType _divineBase;
}
