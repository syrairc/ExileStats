using System;
using System.Collections.Generic;
using System.Linq;
using ExileCore2;
using ExileCore2.PoEMemory;
using ExileCore2.PoEMemory.Components;
using ExileCore2.PoEMemory.Elements;
using ExileCore2.PoEMemory.FilesInMemory;
using ExileCore2.PoEMemory.MemoryObjects;
using ExileCore2.PoEMemory.Models;

namespace ExileStats;

/// <summary>
/// One Expedition runestone's live snapshot, keyed by its encounter-entity grid position so
/// <see cref="ContentTracker"/> can match it to the existing <c>"Expedition"</c> <see cref="ContentSighting"/>
/// by fingerprint. <see cref="Pool"/> is the computed-possible reward set (always available once the runestone
/// streams in); <see cref="Offered"/> is the actual options the game presented in the encounter window (only
/// while it's open, attributed to the runestone the player is standing at).
/// </summary>
public class ExpeditionSiteInfo
{
    public float GridX { get; set; }
    public float GridY { get; set; }
    public int? RuneCount { get; set; }
    public List<ExpeditionReward> Pool { get; set; }
    public List<ExpeditionReward> Offered { get; set; }
}

/// <summary>
/// Reads Expedition runestone data from game memory — rune count + the reward pool computed from
/// <c>Files.Expedition2Recipes</c>/<c>Expedition2RunesWeights</c>, plus the live offered options from
/// <c>IngameUi.Expedition2Window</c>. Ports the reads in the <c>Expedition2Good</c> plugin. All game-coupled
/// reads live here (so <see cref="ContentTracker"/> stays free of the game API), wrapped so a torn entity /
/// missing window / absent NinjaPricer never throws — returns an empty list (or null prices) on failure.
/// </summary>
public static class ExpeditionRewards
{
    private const string EncounterPrefix = "Metadata/MiscellaneousObjects/Expedition2/Expedition2Encounter";

    public static List<ExpeditionSiteInfo> Read(GameController gc)
    {
        var sites = new List<ExpeditionSiteInfo>();
        try
        {
            var ui = gc?.IngameState?.IngameUi;
            var labels = ui?.ItemsOnGroundLabelsVisible;
            if (labels == null)
                return sites;

            var areaLevel = gc.IngameState.Data.CurrentAreaLevel;

            foreach (var lg in labels)
            {
                var ground = lg?.ItemOnGround;
                if (ground?.Metadata?.StartsWith(EncounterPrefix, StringComparison.Ordinal) != true)
                    continue;

                Expedition2EncounterLabel label;
                try { label = lg.Label.AsObject<Expedition2EncounterLabel>(); } catch { continue; }
                if (label == null)
                    continue;

                var pos = ground.GridPos;
                sites.Add(new ExpeditionSiteInfo
                {
                    GridX = pos.X,
                    GridY = pos.Y,
                    RuneCount = label.RuneCount,
                    Pool = ComputePool(label, areaLevel, gc),
                });
            }

            AttachOfferedRewards(gc, ui, sites);
        }
        catch
        {
            // Best-effort: return whatever was gathered before the failure.
        }
        return sites;
    }

    // Port of Expedition2Good's recipe filter: recipes whose RuneCountRequired is allowed by the runestone's
    // fixed rune/slot weights at this area level, the fixed-rune slot matches, and the area level is in range.
    private static List<ExpeditionReward> ComputePool(Expedition2EncounterLabel label, int areaLevel, GameController gc)
    {
        try
        {
            var recipes = gc.Files.Expedition2Recipes.EntriesList;
            var allowedRuneCounts = gc.Files.Expedition2RunesWeights.EntriesList
                .Where(x => x.RuneSlot - 1 == label.FixedRunePosition)
                .Where(x => x.Rune?.Equals(label.FixedRune) == true)
                .Where(x => x.Level <= areaLevel)
                .Select(x => x.SlotCount)
                .ToHashSet();

            return recipes
                .Where(x => x.RuneCountRequired <= label.RuneCount)
                .Where(x => allowedRuneCounts.Contains(x.RuneCountRequired))
                .Where(x => x.MinLevelReq <= areaLevel && x.MaxLevelReq >= areaLevel)
                .Where(x => x.Runes.ElementAtOrDefault(label.FixedRunePosition)?.Equals(label.FixedRune) == true)
                .Select(x => ToReward(x, gc))
                .OrderByDescending(r => r.Value ?? 0)
                .ToList();
        }
        catch
        {
            return null;
        }
    }

    // When the encounter window is open it belongs to the runestone the player is interacting with — attach its
    // options to the activated site nearest the player (maps usually have a single encounter; nearest handles more).
    private static void AttachOfferedRewards(GameController gc, IngameUIElements ui, List<ExpeditionSiteInfo> sites)
    {
        try
        {
            if (sites.Count == 0 || ui?.Expedition2Window is not { IsVisible: true } window)
                return;

            var offered = window.Options
                .Where(o => o is { IsValid: true, IsVisible: true, IsVisibleLocal: true, Recipe: not null })
                .Select(o => ToReward(o.Recipe, gc))
                .OrderByDescending(r => r.Value ?? 0)
                .ToList();
            if (offered.Count == 0)
                return;

            var p = gc.Player.GridPos;
            ExpeditionSiteInfo target = null;
            var best = double.MaxValue;
            foreach (var s in sites)
            {
                var dx = s.GridX - p.X;
                var dy = s.GridY - p.Y;
                var d = dx * dx + dy * dy;
                if (d < best) { best = d; target = s; }
            }
            if (target != null)
                target.Offered = offered;
        }
        catch
        {
            // leave Offered null
        }
    }

    private static ExpeditionReward ToReward(Expedition2Recipe recipe, GameController gc)
    {
        var perUnit = ItemPricer.GetBaseItemTypeValue(recipe.Reward, gc);
        return new ExpeditionReward
        {
            Name = string.IsNullOrWhiteSpace(recipe.Description) ? recipe.Reward?.BaseName : recipe.Description,
            Count = recipe.RewardCount,
            Value = perUnit.HasValue ? perUnit.Value * recipe.RewardCount : (double?)null,
        };
    }
}
