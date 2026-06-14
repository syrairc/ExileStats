using System;
using System.Collections.Generic;

namespace ExileStats;

// Tracker registry + the per-tracker run methods (the bodies that used to be inlined as gated blocks in
// Tick). The dispatcher (TrackerDispatcher) gates/throttles/times each; these just do the work.
public partial class ExileStats
{
    internal void Err(string message) => LogError(message);

    // The one place trackers are declared. Add a monitor = add a line here (no Tick edits, no extra entity
    // pass). Columns: name, entity bucket needed, requires a tracked area, enabled-gate, interval-ms, run.
    private TrackerDispatcher BuildDispatcher() => new(
        new DelegateTracker("Monsters", EntityNeed.Monsters, requiresTrackedArea: true,
            s => s.CountMonsters && (s.ShowCounter || s.LogToFile || s.LogSnapshots || s.LogMonsterPositions),
            s => 0, MonsterScan),
        new DelegateTracker("Loot", EntityNeed.WorldItems, true,
            s => s.LogLoot, s => s.LootScanIntervalMs.Value, LootScan),
        new DelegateTracker("Content", EntityNeed.AllValid, true,
            s => s.LogContent, s => s.ContentScanIntervalMs.Value, ContentScan),
        new DelegateTracker("Wisps", EntityNeed.AllValid, true,
            s => s.LogWisps, s => s.WispScanIntervalMs.Value, WispScan),
        new DelegateTracker("Pickups", EntityNeed.None, true,
            s => s.LogPickups, s => s.PickupScanIntervalMs.Value, PickupScan),
        new DelegateTracker("MapInfo", EntityNeed.None, true,
            s => s.LogToFile, s => 0, MapSideRun),
        new DelegateTracker("Snapshot", EntityNeed.None, true,
            s => s.LogSnapshots, s => s.SnapshotIntervalSeconds.Value * 1000, SnapshotRun),
        new DelegateTracker("Path", EntityNeed.None, true,
            s => s.LogPath, s => 0, PathRun),
        new DelegateTracker("Deaths", EntityNeed.Monsters, true,
            s => s.LogDeaths, s => 0, DeathRun),
        new DelegateTracker("NetWorth", EntityNeed.None, requiresTrackedArea: false,
            s => s.TrackNetWorth, s => 0, NetWorthRun));

    private void MonsterScan(in TrackerContext ctx)
    {
        _monsterCounter.Update(ctx.Buckets.Monsters, ctx.ElapsedSeconds);
        // Drain the buffered first-seen positions to disk in batches (mirrors PathRun) to avoid an
        // O(n^2) whole-file rewrite per new monster.
        if (Settings.LogMonsterPositions && _monsterCounter.BufferedCount >= 32)
            FlushMonsters(ctx.Area.AreaId, ctx.Area.InstanceHash);
    }

    private void LootScan(in TrackerContext ctx)
    {
        var newLoot = _lootTracker.Scan(ctx.Buckets.WorldItems, GameController);
        if (newLoot.Count > 0)
            LootLog.Append(DirectoryFullName, ctx.Area.AreaId, ctx.Area.InstanceHash, newLoot);
    }

    private void ContentScan(in TrackerContext ctx)
    {
        // Lazy callbacks: the tribute HUD / expedition label walk only fire while that mechanic is present.
        var dirty = _contentTracker.Scan(ctx.Buckets.AllValid, ctx.ElapsedSeconds,
            () => RitualTribute.Read(GameController.IngameState.IngameUi),
            () => ExpeditionRewards.Read(GameController),
            () => _ritualRewards.Update(GameController));
        if (dirty.Count > 0)
            ContentLog.Append(DirectoryFullName, ctx.Area.AreaId, ctx.Area.InstanceHash, dirty);
    }

    private void WispScan(in TrackerContext ctx)
    {
        // AllValid, not the Monster bucket: the roaming wisp's entity type isn't confirmed, and the tracker
        // filters monsters itself — so this can't miss a non-Monster-typed wisp.
        var dirty = _wispTracker.Scan(ctx.Buckets.AllValid, ctx.ElapsedSeconds);
        if (dirty.Count > 0)
            WispLog.Append(DirectoryFullName, ctx.Area.AreaId, ctx.Area.InstanceHash, dirty);
    }

    private void PickupScan(in TrackerContext ctx)
    {
        var picks = _pickupTracker.Scan(GameController, ctx.Area.EnteredAt);
        if (picks.Count > 0)
            PickupLog.Append(DirectoryFullName, ctx.Area.AreaId, ctx.Area.InstanceHash, picks);
    }

    // Read the map objectives/content from the side panel once it has populated, then freeze for the run.
    private void MapSideRun(in TrackerContext ctx)
    {
        if (_mapInfoCaptured)
            return;
        var (objectives, content) = MapSideInfo.Read(GameController);
        if (objectives.Count > 0 || content.Count > 0)
        {
            _currentArea.MapObjectives = objectives;
            _currentArea.MapContent = content;
            _mapInfoCaptured = true;
        }
    }

    private void SnapshotRun(in TrackerContext ctx) => TryLogSnapshot();

    // High-rate player-position sampling for an accurate path (buffered; flushed in batches).
    private void PathRun(in TrackerContext ctx)
    {
        var player = GameController.Player;
        if (player is not { IsValid: true })
            return;
        var pos = player.GridPos;
        var elapsed = Math.Round(ctx.ElapsedSeconds, 2);
        _pathTracker.Sample(pos.X, pos.Y, elapsed, Settings.PathIntervalMs.Value,
            Settings.PathStepUnits.Value, _currentArea.AreaWidth, _currentArea.AreaHeight);
        if (_pathTracker.BufferedCount >= 64)
            FlushPath();
    }

    private void DeathRun(in TrackerContext ctx) => TryLogDeath(ctx.Buckets.Monsters);

    // Net worth: while the stash panel is open, scan every loaded tab (+ backpack) and log net worth. Runs in
    // towns/hideouts too (RequiresTrackedArea=false) — that's where the stash is opened.
    private void NetWorthRun(in TrackerContext ctx)
    {
        var stash = _ingameUi?.StashElement;
        if (stash is not { IsVisible: true })
            return;

        _stashTracker.Update(GameController, Settings.NetWorthIncludeInventory.Value);

        if (DateTime.Now < _nextNetWorthWriteAt)
            return;

        var ex = _stashTracker.TotalExalted;
        _lastDivineRate = ItemPricer.GetDivineRate(GameController);

        // Skip partial-tab reads: a total that craters below a fraction of the session peak means not all
        // tabs have streamed in (or pricing isn't ready). Don't persist it to tabs.json or networth.json.
        if (_netWorthPeakExalted > 0 && ex < _netWorthPeakExalted * MinNetWorthFraction)
        {
            _nextNetWorthWriteAt = DateTime.Now.AddSeconds(Settings.NetWorthIntervalSeconds.Value);
            return;
        }
        if (ex > _netWorthPeakExalted)
            _netWorthPeakExalted = ex;

        StashLog.WriteTabs(DirectoryFullName, _stashTracker.Tabs);

        // Append a time-series point only on a meaningful move (avoid flat-line spam).
        if (!_netWorthInitialized || Math.Abs(ex - _lastNetWorthExalted) > 0.5)
        {
            StashLog.AppendNetWorth(DirectoryFullName, new NetWorthPoint
            {
                At = DateTime.Now,
                TotalExalted = ex,
                TotalDivine = _lastDivineRate is > 0 ? ex / _lastDivineRate.Value : null,
                DivineRate = _lastDivineRate,
                TabCount = _stashTracker.TabCount,
                ItemCount = _stashTracker.ItemCount,
                IncludesInventory = Settings.NetWorthIncludeInventory.Value,
            });
            _lastNetWorthExalted = ex;
            _netWorthInitialized = true;
        }

        _nextNetWorthWriteAt = DateTime.Now.AddSeconds(Settings.NetWorthIntervalSeconds.Value);
    }
}
