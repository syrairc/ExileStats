namespace ExileStats;

/// <summary>
/// One favour (reward) offered in the Ritual reward window (<c>IngameUi.RitualWindow.Items</c>) during a map.
/// Each favour is a real item Entity, so it reuses <see cref="ItemRecord.Read{T}"/> for name/rarity/value
/// (<see cref="ItemRecord.ChaosValue"/> via NinjaPricer). <see cref="Purchased"/> flips true when the favour
/// leaves the window and a matching item appears in the player's inventory. Stored (union across rerolls) on
/// every <c>Ritual</c> <see cref="ContentSighting"/> in the instance.
/// </summary>
public class RitualReward : ItemRecord
{
    public bool Purchased { get; set; }

    /// <summary>Dedup key for the offered-favour union (identity, ignores the rotating entity Id).</summary>
    public string Key() => $"{(string.IsNullOrEmpty(UniqueName) ? BaseName : UniqueName)}|{Rarity}|{StackSize}";
}
