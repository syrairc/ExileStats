using System;
using System.Collections.Generic;
using ExileCore2;
using ExileCore2.PoEMemory.Components;
using ExileCore2.PoEMemory.MemoryObjects;

namespace ExileStats;

/// <summary>
/// Common item data shared by <see cref="LootItem"/> (seen on the ground) and <see cref="PickupItem"/>
/// (picked up into the backpack). Both read the same underlying item Entity, so the component-extraction
/// lives here once in <see cref="Read{T}"/>.
/// </summary>
public abstract class ItemRecord
{
    public string Path { get; set; }
    public string BaseName { get; set; }
    public string ClassName { get; set; }

    public string Rarity { get; set; }
    public int ItemLevel { get; set; }
    public bool Identified { get; set; }
    public string UniqueName { get; set; }
    public int Quality { get; set; }
    public int Sockets { get; set; }
    public int StackSize { get; set; }
    public int MapTier { get; set; }
    public bool Corrupted { get; set; }
    public List<string> Mods { get; set; }

    /// <summary>NinjaPricer <c>MinChaosValue</c> for this item (full-stack value of the entity). Null when
    /// the pricer bridge is unavailable. See <see cref="ItemPricer"/>.</summary>
    public double? ChaosValue { get; set; }

    /// <summary>Builds a record of type <typeparamref name="T"/> from an item Entity (the real item, i.e.
    /// a ground <c>WorldItem.ItemEntity</c> or an inventory <c>ServerInventory.Items</c> entry). Fills only
    /// the shared item-data fields; callers set their own identity/location/timing fields.</summary>
    public static T Read<T>(Entity itemEntity, GameController gc) where T : ItemRecord, new()
    {
        var item = new T { Path = itemEntity.Path };

        var baseType = gc.Files.BaseItemTypes.Translate(itemEntity.Path);
        item.BaseName = baseType?.BaseName ?? "";
        item.ClassName = baseType?.ClassName ?? "";

        if (itemEntity.TryGetComponent<Mods>(out var mods))
        {
            item.Rarity = mods.ItemRarity.ToString();
            item.ItemLevel = mods.ItemLevel;
            item.Identified = mods.Identified;
            item.UniqueName = mods.UniqueName;
            item.Mods = mods.EnchantedStats;
        }

        if (itemEntity.TryGetComponent<Base>(out var @base))
        {
            item.Corrupted = @base.isCorrupted;
            if (item.ItemLevel == 0)
                item.ItemLevel = @base.CurrencyItemLevel;
        }

        if (itemEntity.TryGetComponent<Quality>(out var quality))
            item.Quality = quality.ItemQuality;

        if (itemEntity.TryGetComponent<Sockets>(out var sockets))
        {
            try { item.Sockets = sockets.NumberOfSockets; } catch { /* not socketable */ }
        }

        if (itemEntity.TryGetComponent<Stack>(out var stack))
            item.StackSize = stack.Size;

        if (itemEntity.TryGetComponent<Map>(out var map))
            item.MapTier = map.Tier;

        // Best-effort value (stays null if NinjaPricer isn't loaded / hasn't priced yet).
        item.ChaosValue = ItemPricer.GetChaosValue(itemEntity, gc);

        return item;
    }
}
