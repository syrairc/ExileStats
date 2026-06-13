using System;
using System.Drawing;
using System.Linq;
using ExileCore2;
using ExileCore2.PoEMemory.Components;
using ExileCore2.PoEMemory.Elements;
using ExileCore2.PoEMemory.MemoryObjects;
using ExileCore2.Shared.Enums;
using Vector2 = System.Numerics.Vector2;

namespace ExileStats;

public class ExileStats : BaseSettingsPlugin<ExileStatsSettings>
{
    private readonly MonsterCounter _monsterCounter = new();
    private MapRunRecord _currentArea;
    private DateTime _lastSettingsDraw;
    private IngameUIElements _ingameUi;

    public override bool Initialise()
    {
        // Capture the current area now so the counter works when the plugin loads mid-map.
        try
        {
            if (GameController?.Area?.CurrentArea is { } cur)
                _currentArea = CaptureArea(cur);
        }
        catch { /* not in an area yet */ }

        return true;
    }

    public override void AreaChange(AreaInstance area)
    {
        // Leaving a map back to the hideout: log the finished map's stats + counts.
        if (Settings.LogToFile && _currentArea is { IsMapArea: true } && area.IsHideout)
            TryLogMapRun();

        _monsterCounter.Reset();

        // Snapshot the new area's metadata now, while its memory is valid, so the next transition can
        // log it after we've left it.
        _currentArea = CaptureArea(area);
    }

    public override void Tick()
    {
        _monsterCounter.Update(GameController.Entities);
        _ingameUi = GameController.Game.IngameState.IngameUi;

        // Keep the map's monster level fresh (ServerData can be stale at the AreaChange tick).
        if (_currentArea is { IsMapArea: true })
        {
            try { _currentArea.MonsterLevel = GameController.IngameState.ServerData.MonsterLevel; }
            catch { /* server data not ready */ }
        }
    }

    public override void Render()
    {
        if (!GameController.InGame)
            return;

        DrawMonsterCount();
    }

    // ExileCore calls this only while this plugin's settings page is open; timestamp it so Render can
    // tell whether the config is currently visible.
    public override void DrawSettings()
    {
        _lastSettingsDraw = DateTime.Now;
        base.DrawSettings();
    }

    // ---- Area capture & logging ----

    private MapRunRecord CaptureArea(AreaInstance area)
    {
        var wa = area.Area;
        var areaId = wa?.Id ?? "";
        var record = new MapRunRecord
        {
            EnteredAt = area.TimeEntered,
            Name = area.Name,
            DisplayName = area.DisplayName,
            Act = area.Act,
            RealLevel = area.RealLevel,
            IsTown = area.IsTown,
            IsHideout = area.IsHideout,
            IsPeaceful = area.IsPeaceful,
            HasWaypoint = area.HasWaypoint,
            InstanceHash = area.Hash,
            ZoneSwitchId = area.ZoneSwitchId,
            AreaId = areaId,
            Index = wa?.Index ?? 0,
            AreaLevel = wa?.AreaLevel ?? 0,
            WorldAreaId = wa?.WorldAreaId ?? 0,
            IsUnique = wa?.IsUnique ?? false,
            IsMapArea = areaId.StartsWith("Map", StringComparison.Ordinal),
        };

        // Session/server baselines (Gold & XP are account-wide → captured here for an end-of-run delta).
        try
        {
            var sd = GameController.IngameState.ServerData;
            record.League = sd.League;
            record.MonsterLevel = sd.MonsterLevel;
            record.CharacterLevel = sd.CharacterLevel;
            record.ServerInstanceId = sd.InstanceId;
            record.GoldStart = sd.Gold;
            record.XpStart = GameController.Player?.GetComponent<Player>()?.XP ?? 0;
        }
        catch { /* server data not ready yet */ }

        return record;
    }

    private void TryLogMapRun()
    {
        try
        {
            var record = _currentArea;
            var now = DateTime.Now;
            record.LoggedAt = now;
            record.DurationSeconds = Math.Round((now - record.EnteredAt).TotalSeconds, 1);

            // End-of-run server values + deltas.
            try
            {
                var sd = GameController.IngameState.ServerData;
                record.GoldEnd = sd.Gold;
                record.GoldGained = record.GoldEnd - record.GoldStart;
                record.XpEnd = GameController.Player?.GetComponent<Player>()?.XP ?? 0;
                record.XpGained = record.XpEnd - record.XpStart;
            }
            catch { /* server data unavailable */ }

            record.MonstersTotal = _monsterCounter.SeenTotal;
            record.White = _monsterCounter.Seen(MonsterRarity.White);
            record.Magic = _monsterCounter.Seen(MonsterRarity.Magic);
            record.Rare = _monsterCounter.Seen(MonsterRarity.Rare);
            record.Unique = _monsterCounter.Seen(MonsterRarity.Unique);
            record.MonstersByType = _monsterCounter.SeenByTypeSnapshot();
            record.UniqueMonsters = _monsterCounter.UniqueNamesSnapshot();

            MapMonsterLog.Append(DirectoryFullName, record);
        }
        catch (Exception ex)
        {
            LogError($"ExileStats -> Failed to log map run: {ex}");
        }
    }

    // ---- On-screen counter ----

    private static readonly (MonsterRarity Rarity, string Label, Color Color)[] CounterRows =
    [
        (MonsterRarity.White, "White", Color.White),
        (MonsterRarity.Magic, "Magic", Color.FromArgb(136, 136, 255)),
        (MonsterRarity.Rare, "Rare", Color.FromArgb(255, 255, 119)),
        (MonsterRarity.Unique, "Unique", Color.DarkOrange),
    ];

    private void DrawMonsterCount()
    {
        if (!Settings.ShowCounter)
            return;

        // Show only inside maps, or while the config is open (so it can be positioned); never on top of
        // large/fullscreen panels (inventory, atlas, etc.).
        var configOpen = (DateTime.Now - _lastSettingsDraw).TotalMilliseconds < 250;
        if (!configOpen)
        {
            if (_currentArea is not { IsMapArea: true })
                return;
            if (_ingameUi != null &&
                (_ingameUi.FullscreenPanels.Any(x => x.IsVisible) ||
                 _ingameUi.LargePanels.Any(x => x.IsVisible)))
                return;
        }

        var pos = new Vector2(Settings.PositionX.Value, Settings.PositionY.Value);
        var header = $"Monsters: {_monsterCounter.SeenTotal}  (alive {_monsterCounter.AliveTotal})";
        pos.Y += Graphics.DrawText(header, pos, Settings.TextColor.Value).Y;

        if (!Settings.SplitByRarity.Value)
            return;

        foreach (var (rarity, label, color) in CounterRows)
        {
            var line = $"  {label}: {_monsterCounter.Seen(rarity)}  (alive {_monsterCounter.Alive(rarity)})";
            pos.Y += Graphics.DrawText(line, pos, color).Y;
        }
    }
}
