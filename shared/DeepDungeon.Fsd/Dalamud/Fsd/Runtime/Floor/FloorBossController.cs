using System;
using System.Numerics;
using DeepDungeon.Fsd.Core;
using DeepDungeon.Fsd.Dalamud.Runtime.Helpers;
using DeepDungeon.Fsd.Dalamud.Runtime.Navigation;
using DeepDungeon.Fsd.Dalamud.Utilities;
using global::Dalamud.Game.ClientState.Conditions;
using global::Dalamud.Game.ClientState.Objects.SubKinds;
using global::Dalamud.Game.ClientState.Objects.Types;
using FFXIVClientStructs.FFXIV.Client.Game.InstanceContent;
using MasterPlayer.MechanicRuntime;

namespace DeepDungeon.Fsd.Dalamud.Runtime.Floor;

/// <summary>Owns one floor's boss navigation, combat buffs and diagnostic cadence.</summary>
internal sealed class FloorBossController : IDisposable
{
    private const float BossNavigationArrivalTolerance = 3f;
    private const uint SteelStatusId = 1100;
    private const uint StrengthStatusId = 687;
    private const uint PtHasteStatusId = 4718;
    private readonly byte _floor;
    private readonly RunContext _ctx;
    private readonly NavigationHelper _navHelper;
    private readonly PomanderManager _pomanderManager;
    private readonly FloorItemExecutor _items;
    private readonly DirectMovementController _preparationHold = new("boss preparation");
    private bool _preparing;
    // These helpers retain their run lifetime; this floor owner does not dispose or recreate them.
    private readonly Func<string, FloorObjectiveKind, bool> _requireMovementPermission;
    private readonly Action<string> _setStatus;
    private readonly Action<string, object> _recordReplayEvent;
    private readonly Action<byte, IPlayerCharacter> _recordBossSnapshot;
    private DateTime _nextBossDiagnosticAtUtc = DateTime.MinValue;
    private readonly Func<bool>? _coordEscapeActive;
    private readonly IBossMovementStrategy? _movementStrategy;
    private string? _movementFailure;
    internal string? MovementFailure => _movementFailure;
    private EncounterReadiness? _lastMechanicReadiness;
    private bool _movementStarted;
    internal bool UsesSpinStrategy { get; }
    internal string MovementStrategyName => _movementStrategy?.Name ?? "spin";
    internal bool MovementStarted => _movementStarted;
    internal bool AllowsCombatAssist => _movementFailure == null &&
        (UsesSpinStrategy || _lastMechanicReadiness?.Kind == EncounterReadinessKind.Ready);
    internal bool RequestsNorthCamera => _movementStarted && _movementStrategy?.RequestsNorthCamera == true;
    private bool BossNavigationResolved { get; set; }
    private string Status { set => _setStatus(value); }
    private FarmingItemPolicy ItemPolicy => FarmingItemPolicy.For(_ctx.FarmingPlan?.Mode);

    public FloorBossController(byte floor, RunContext context, NavigationHelper navigation,
        PomanderManager pomanders, FloorItemExecutor items,
        Func<string, FloorObjectiveKind, bool> requireMovementPermission, Action<string> setStatus,
        Action<string, object> recordReplayEvent, Action<byte, IPlayerCharacter> recordBossSnapshot,
        FloorExplorationController floorRuntime,
        Func<IBossMovementStrategy>? spinBossMovementFactory = null,
        Func<bool>? coordEscapeActive = null)
    {
        _floor = floor;
        _ctx = context;
        _navHelper = navigation;
        _coordEscapeActive = coordEscapeActive;
        _pomanderManager = pomanders;
        _items = items;
        _requireMovementPermission = requireMovementPermission;
        _setStatus = setStatus;
        _recordReplayEvent = recordReplayEvent;
        _recordBossSnapshot = recordBossSnapshot;
        UsesSpinStrategy = floorRuntime.Kind == FloorRuntimeKind.Boss &&
            DeepDungeonBossPolicy.UsesSpin(floorRuntime.DungeonId, floor);
        if (!UsesSpinStrategy)
            _movementStrategy = new NormalBossMovementStrategy(floorRuntime);
        else
        {
            try
            {
                _movementStrategy = spinBossMovementFactory?.Invoke();
                if (_movementStrategy == null) _movementFailure = "Required spin movement factory is unavailable.";
            }
            catch (Exception ex) { _movementFailure = $"Spin initialization failed: {ex.Message}"; }
            if (_movementFailure != null) Service.Log.Error($"[FloorPhase] {_movementFailure}");
        }
    }

    public void Dispose()
    {
        try { SuspendMovement(); }
        finally { _preparationHold.Dispose(); }
    }

    private void StopMovement()
    {
        if (!_movementStarted) return;
        _movementStarted = false;
        try { _movementStrategy?.Stop(); }
        finally { _navHelper.Cancel(); }
        _recordReplayEvent("boss-movement-stopped", new { floor = _floor, strategy = MovementStrategyName });
    }

    internal void SuspendMovement()
    {
        try { StopMovement(); }
        finally
        {
            _preparing = false;
            _preparationHold.SuppressInput = false;
        }
    }

    public unsafe void Update(InstanceContentDeepDungeon* dd)
    {
        if (_movementFailure != null || _movementStrategy == null)
        {
            SuspendMovement();
            _navHelper.Cancel();
            Status = $"Boss floor - fault ({MovementStrategyName}): {_movementFailure}";
            return;
        }
        if (Service.LocalPlayer is not { IsDead: false } || _ctx.Duty.IsTransitioning ||
            Service.Condition[ConditionFlag.BetweenAreas] || Service.Condition[ConditionFlag.BetweenAreas51])
        {
            SuspendMovement();
            Status = "Boss floor - waiting for player or floor";
            return;
        }
        if (_ctx.Duty.PassageOpen)
        {
            SuspendMovement();
            return;
        }
        if (!_requireMovementPermission("boss objective", FloorObjectiveKind.DefeatBoss))
        {
            SuspendMovement();
            return;
        }
        // Preparation owns the hold before either movement strategy can start.
        if (TryPrepareApproach(dd))
        {
            StopMovement();
            return;
        }
        try
        {
            if (!_movementStarted)
            {
                _navHelper.Cancel();
                _movementStarted = true;
                _movementStrategy.Start();
                _recordReplayEvent("boss-movement-started", new { floor = _floor, strategy = _movementStrategy.Name });
            }
            var status = _movementStrategy.Update(dd);
            if (status != null) Status = status;
            if (UsesSpinStrategy)
                UpdateManagedEncounter(dd, status, liveEncounter: true);
        }
        catch (Exception ex)
        {
            _movementFailure = $"{MovementStrategyName} execution failed: {ex.Message}";
            Service.Log.Error(ex, $"[FloorPhase] {_movementFailure}");
            try { SuspendMovement(); }
            catch (Exception cleanup) { Service.Log.Error(cleanup, "[FloorPhase] failed boss movement cleanup"); }
            Status = $"Boss floor - fault: {_movementFailure}";
        }
    }

    internal void ReportMechanicReadiness(EncounterReadiness readiness)
    {
        if (_lastMechanicReadiness is { } previous && previous.Kind == readiness.Kind && previous.Detail == readiness.Detail) return;
        _lastMechanicReadiness = readiness;
        _recordReplayEvent("boss-mechanic-readiness", new { floor = _floor, state = readiness.Kind.ToString(), readiness.Detail, readiness.ObservedAt });
        if (readiness.Failed) Service.Log.Error($"[FloorPhase] Boss mechanics {readiness.Kind}: {readiness.Detail}");
        else Service.Log.Info($"[FloorPhase] Boss mechanics {readiness.Kind}: {readiness.Detail}");
    }

    // Finish items before approaching: a cast/animation lock after spin has
    // established its phase can let the boss face the player and start a skill.
    internal unsafe bool TryPrepareApproach(InstanceContentDeepDungeon* dd)
    {
        var actions = FFXIVClientStructs.FFXIV.Client.Game.ActionManager.Instance();
        bool beforeCombat = !_ctx.Duty.PassageOpen && !Service.Condition[ConditionFlag.InCombat] &&
            Service.LocalPlayer is { IsDead: false };
        bool needed = beforeCombat && (_items.Session.Pending != null ||
            Service.LocalPlayer?.IsCasting == true || Service.Condition[ConditionFlag.Casting] ||
            actions != null && actions->AnimationLock > 0 ||
            TryMaintainBossBuffs(dd));
        if (_preparing != needed)
        {
            _preparing = needed;
            _preparationHold.SuppressInput = needed;
            _recordReplayEvent("boss-preparation", new { floor = _floor, waiting = needed });
        }
        if (!needed) return false;
        Status = "Boss floor - preparing items before approach";
        return true;
    }

    // Keep the existing one-second diagnostic cadence for both strategies.
    private void RecordBossCombatSnapshot()
    {
        var player = Service.LocalPlayer;
        var now = DateTime.UtcNow;
        if (player == null || now < _nextBossDiagnosticAtUtc) return;
        _nextBossDiagnosticAtUtc = now.AddSeconds(1);
        _recordBossSnapshot(_floor, player);
    }

    /// <summary>Maintenance-only update for the selected boss movement
    /// path: keeps the combat snapshot and buff maintenance while a live
    /// observed boss exists, sets status — NO Navigate/Cancel on the
    /// shared travel helper.</summary>
    public unsafe void UpdateManagedEncounter(InstanceContentDeepDungeon* dd,
        string? status, bool liveEncounter)
    {
        Status = status ?? "Boss floor - managed encounter";
        if (liveEncounter)
        {
            RecordBossCombatSnapshot();
            if (Service.Condition[ConditionFlag.InCombat]) TryMaintainBossBuffs(dd);
        }
    }

    internal unsafe void UpdateApproach(InstanceContentDeepDungeon* dd)
    {
        var player = Service.LocalPlayer;
        if (player == null)
        {
            Status = "Boss floor - waiting for player";
            return;
        }

        if (Service.Condition[ConditionFlag.BetweenAreas] ||
            Service.Condition[ConditionFlag.BetweenAreas51])
        {
            Status = "Boss floor - waiting for floor load to complete";
            _navHelper?.Cancel();
            return;
        }
        if (!_requireMovementPermission("boss objective", FloorObjectiveKind.DefeatBoss))
            return;

        RecordBossCombatSnapshot();
        // Coord escape owns movement through the shared boundary — every
        // _navHelper write below (Navigate AND Cancel, they share the helper)
        // must stay silent while it holds.
        if (_coordEscapeActive?.Invoke() == true)
        {
            Status = "Boss floor - internal mechanic escape";
            return;
        }

        if (Service.Condition[ConditionFlag.InCombat])
        {
            TryMaintainBossBuffs(dd);
            if (!BossNavigationResolved)
            {
                _navHelper?.Cancel();
                BossNavigationResolved = true;
                Service.Log.Info("[FloorPhase] Boss combat started -> canceling boss navigation");
            }

            Status = "Boss floor - combat assist active";
            return;
        }

        var boss = Runtime.Helpers.CombatTargetingHelpers.PickNearestHostile(60f, out _);
        if (boss == null)
        {
            Status = "Boss floor - waiting for boss target";
            return;
        }

        if (BossNavigationResolved)
        {
            var currentTarget = Service.TargetManager.Target as IBattleChara;
            if (currentTarget != null &&
                currentTarget.GameObjectId == boss.GameObjectId &&
                !currentTarget.IsDead &&
                IsWithinBossEngageRadius(player.Position, boss.Position))
            {
                Status = "Boss floor - waiting for boss combat";
                return;
            }

            BossNavigationResolved = false;
            _recordReplayEvent("boss-navigation-reset", new
            {
                floor = dd->Floor,
                reason = "combat-ended-or-target-lost",
                bossId = boss.GameObjectId,
                currentTargetId = currentTarget?.GameObjectId ?? 0
            });
        }

        var withinRange = IsWithinBossEngageRadius(player.Position, boss.Position);
        if (withinRange)
        {
            _navHelper?.Cancel();
            BossNavigationResolved = true;
            Status = "Boss floor - at boss";
            Service.Log.Info("[FloorPhase] Boss navigation complete -> in engage range");
            return;
        }

        var state = _navHelper!.Navigate(boss.Position, player.Position, BossNavigationArrivalTolerance, retryIntervalSeconds: 5.0);
        switch (state)
        {
            case NavigationState.Moving:
                Status = "Boss floor - navigating to boss";
                break;
            case NavigationState.Arrived:
                BossNavigationResolved = true;
                Status = "Boss floor - at boss";
                Service.Log.Info("[FloorPhase] Boss navigation complete -> arrived");
                break;
            case NavigationState.StuckRepathing:
                Status = $"Boss floor - repathing to boss ({_navHelper.StuckRetryCount}/3)";
                break;
            case NavigationState.StuckGiveUp:
                BossNavigationResolved = true;
                Status = "Boss floor - boss navigation failed";
                Service.Log.Warning("[FloorPhase] Boss navigation gave up");
                break;
            case NavigationState.Failed:
                BossNavigationResolved = true;
                Status = "Boss floor - boss navigation unavailable";
                Service.Log.Warning("[FloorPhase] Boss navigation failed to start");
                break;
        }
    }

    private static bool IsWithinBossEngageRadius(Vector3 playerPosition, Vector3 bossPosition)
    {
        var dx = playerPosition.X - bossPosition.X;
        var dz = playerPosition.Z - bossPosition.Z;
        return dx * dx + dz * dz <=
               BossNavigationArrivalTolerance * BossNavigationArrivalTolerance;
    }

    private bool CombatBuffUsable(uint slot, bool overcapRelief = false) =>
        _items.IsPomanderAvailableForFloorUse(slot) && ItemPolicy.AllowsCombatBuff(
            _pomanderManager.GetCount(slot), _ctx?.Duty.IsBossFloor == true, overcapRelief);

    private static float LocalStatusRemaining(uint statusId, bool zeroMeansPermanent = false)
    {
        var player = Service.LocalPlayer;
        if (player != null)
            foreach (var status in player.StatusList)
                if (status.StatusId == statusId)
                    return zeroMeansPermanent && status.RemainingTime == 0 ? float.PositiveInfinity : Math.Abs(status.RemainingTime);
        return 0;
    }

    private unsafe bool TryMaintainBossBuffs(InstanceContentDeepDungeon* dd)
    {
        if (_movementStrategy?.SuppressCombatItems == true && Service.Condition[ConditionFlag.InCombat]) return false;
        if (!_items.CanAttemptPomanderUse()) return false;
        // Defence first. Refresh shortly before expiry rather than repeatedly overwriting active buffs.
        if (CombatBuffUsable(FloorInitPlanner.SteelPomanderSlotIndex) &&
            FarmingItemPolicy.NeedsBossRefresh(LocalStatusRemaining(SteelStatusId)) &&
            _items.TryUsePomander(FloorInitPlanner.SteelPomanderSlotIndex, dd, "boss steel refresh")) return true;
        if (CombatBuffUsable(FloorInitPlanner.StrengthPomanderSlotIndex) &&
            FarmingItemPolicy.NeedsBossRefresh(LocalStatusRemaining(StrengthStatusId)) &&
            _items.TryUsePomander(FloorInitPlanner.StrengthPomanderSlotIndex, dd, "boss strength refresh")) return true;
        if (dd->DeepDungeonId == 4 && CombatBuffUsable(FloorInitPlanner.HastePomanderSlotIndex) &&
            FarmingItemPolicy.NeedsBossRefresh(LocalStatusRemaining(PtHasteStatusId)) &&
            _items.TryUsePomander(FloorInitPlanner.HastePomanderSlotIndex, dd, "boss haste refresh")) return true;
        // The PT incense grants Tranquil Moonlight for this floor.
        return dd->DeepDungeonId == 4 &&
            ItemPolicy.AllowsBossSerenity(_items.GetStoneCountAvailableForFloorUse(3), _ctx.Duty.IsBossFloor,
                LocalStatusRemaining(4584, zeroMeansPermanent: true),
                _ctx.Knowledge.Floor?.WasStoneUseConfirmed(3) == true) &&
            _items.TryDispatchFloorStone(3, dd, "boss serenity incense", FloorItemUsePurpose.BossSerenity);
    }
}
