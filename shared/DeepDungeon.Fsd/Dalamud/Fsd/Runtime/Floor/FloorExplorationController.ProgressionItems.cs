using DeepDungeon.Fsd.Core;
using DeepDungeon.Fsd.Runtime;
using global::Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.Game.InstanceContent;

namespace DeepDungeon.Fsd.Dalamud.Runtime.Floor;

internal sealed partial class FloorExplorationController
{
    private bool UsesProgressionItemPolicy => _ctx.FarmingPlan?.Mode == FarmingMode.DeepProgression;
    private DateTime _nextProgressionItemCheck;
    private (ProgressionItemSnapshot Snapshot, ProgressionItemDecision Decision, byte Status, byte Ban,
        byte Danger, byte ClearStone, string Outcome)? _lastProgressionItemTrace;

    private bool UsedCurrentFloorProgressionItem()
    {
        var knowledge = _ctx.Knowledge.Floor;
        if (knowledge == null) return false;
        if (knowledge.WasPomanderUseConfirmed(FloorInitPlanner.SerenityPomanderSlotIndex)) return true;
        return DungeonId switch
        {
            2 => knowledge.WasPomanderUseConfirmed(ProgressionItemPolicy.FrailtySlot) ||
                 knowledge.WasStoneUseConfirmed(1) || knowledge.WasStoneUseConfirmed(2) ||
                 knowledge.WasStoneUseConfirmed(3) || knowledge.WasStoneUseConfirmed(4),
            4 => knowledge.WasStoneUseConfirmed(1) || knowledge.WasStoneUseConfirmed(2),
            _ => false
        };
    }

    private bool CanSpendProgressionCurrentFloorItem() =>
        ProgressionItemPolicy.CanSpendOnCurrentFloor(_ctx.Duty.PassageOpen, UsedCurrentFloorProgressionItem());

    internal unsafe bool UpdateProgressionItems(InstanceContentDeepDungeon* dd)
    {
        if (!UsesProgressionItemPolicy || Kind != FloorRuntimeKind.Normal ||
            _phase is not (FloorPhase.FloorSetup or FloorPhase.FloorActive) ||
            Service.LocalPlayer is not { IsDead: false })
            return false;

        if (AwaitingFloorClearRelease()) return true;
        bool passageOpen = _ctx.Duty.PassageOpen;
        // Flight must settle before a floor transition can retire its confirmation.
        bool beforeDeparture = passageOpen && !Service.Condition[ConditionFlag.InCombat];
        if (ItemUse.Pending is { } pending)
            return pending.Purpose == FloorItemUsePurpose.Progression &&
                (_phase == FloorPhase.FloorSetup ||
                 beforeDeparture && pending.Key.ItemId == FloorInitPlanner.FlightPomanderSlotIndex);

        var now = DateTime.UtcNow;
        if (now < _nextProgressionItemCheck && _lastProgressionItemTrace?.Snapshot.PassageOpen == passageOpen)
            return false;
        _nextProgressionItemCheck = now + GeneralTickInterval;

        int lastFloor = _ctx.FarmingPlan!.MaxFloor;
        var stage = ProgressionItemPolicy.Stage(Floor, lastFloor);
        var pressure = ProgressionItemPolicy.Pressure(dd->DeepDungeonStatusId, dd->DeepDungeonBanId, dd->DeepDungeonDangerId);
        var next = FloorItemExecutor.NextFloorKind(dd);
        int remainingMobFloors = 0;
        int floorsetEnd = Math.Min(lastFloor, (Floor + 9) / 10 * 10);
        for (int floor = Floor; floor <= floorsetEnd; floor++)
            if (DeepDungeonFloorClassifier.Classify(DungeonId, floor) == DeepDungeonFloorKind.Mob)
                remainingMobFloors++;

        byte clearStone = 0;
        int clearStock = 0;
        if (DungeonId == 2)
        {
            clearStock = Coord.SiteSearchBoundary.ReadStoneCount(dd);
            clearStone = FindAvailableHohMagicite(dd);
        }
        else if (DungeonId == 4)
        {
            int poisonfruit = GetStoneCountAvailableForFloorUse(1);
            int mazeroot = GetStoneCountAvailableForFloorUse(2);
            clearStock = _pomanderManager.GetStoneCount(1) + _pomanderManager.GetStoneCount(2);
            if (!passageOpen && clearStock > 0 && !Survey.NaturalPoisonfruitAttempted &&
                !Survey.NaturalMazerootAttemptedOrAdopted &&
                DeepDungeonFloorItemUsePolicy.CanUsePtIncense(dd->DeepDungeonBanId) &&
                Survey.CanDispatchNaturalPassageOpeningStoneSafely(dd))
                clearStone = poisonfruit > 0 ? (byte)1 : mazeroot > 0 ? (byte)2 : (byte)0;
        }

        var snapshot = new ProgressionItemSnapshot(stage, pressure, remainingMobFloors, passageOpen,
            UsedCurrentFloorProgressionItem(), next == DeepDungeonFloorKind.Mob, next == DeepDungeonFloorKind.Result,
            new(clearStock, clearStone != 0),
            ReadProgressionStock(dd, FloorInitPlanner.SerenityPomanderSlotIndex),
            DungeonId == 2 ? ReadProgressionStock(dd, ProgressionItemPolicy.FrailtySlot,
                _pomanderManager.IsActive(ProgressionItemPolicy.FrailtySlot)) : default,
            ReadProgressionStock(dd, FloorInitPlanner.FlightPomanderSlotIndex,
                _pomanderManager.IsActive(FloorInitPlanner.FlightPomanderSlotIndex) ||
                _ctx.Knowledge.Floor?.WasPomanderUseConfirmed(FloorInitPlanner.FlightPomanderSlotIndex) == true),
            ReadProgressionStock(dd, FloorInitPlanner.StrengthPomanderSlotIndex, HasLocalPlayerStatus(StrengthStatusId)),
            DungeonId == 4 ? ReadProgressionStock(dd, FloorInitPlanner.HastePomanderSlotIndex,
                HasLocalPlayerStatus(PtHasteStatusId) || _pomanderManager.IsActive(FloorInitPlanner.HastePomanderSlotIndex)) : default);
        var decision = ProgressionItemPolicy.Decide(snapshot);
        if (decision.Item == ProgressionItem.None)
        {
            RecordProgressionItemDecision(dd, snapshot, decision, clearStone, "hold");
            return false;
        }

        if (CurrentInteractionAttempt is { PendingGoldOvercapSlotIndex: not null } or { PendingSilverOvercapStoneRowId: not null })
        {
            RecordProgressionItemDecision(dd, snapshot, decision, clearStone, "chest-overcap-pending");
            return false;
        }
        if (!CanAttemptPomanderUse())
        {
            RecordProgressionItemDecision(dd, snapshot, decision, clearStone, "action-not-ready");
            return false;
        }

        bool dispatched = decision.Item == ProgressionItem.FloorClear
            ? TryUseProgressionFloorClear(dd, clearStone, decision.Reason)
            : TryUsePomander(decision.Item switch
            {
                ProgressionItem.Serenity => FloorInitPlanner.SerenityPomanderSlotIndex,
                ProgressionItem.Frailty => ProgressionItemPolicy.FrailtySlot,
                ProgressionItem.Flight => FloorInitPlanner.FlightPomanderSlotIndex,
                ProgressionItem.Strength => FloorInitPlanner.StrengthPomanderSlotIndex,
                ProgressionItem.Haste => FloorInitPlanner.HastePomanderSlotIndex,
                _ => throw new InvalidOperationException($"Unknown progression item {decision.Item}.")
            }, dd, decision.Reason, FloorItemUsePurpose.Progression);
        RecordProgressionItemDecision(dd, snapshot, decision, clearStone, dispatched ? "dispatched" : "rejected");
        return dispatched && (_phase == FloorPhase.FloorSetup ||
            decision.Item == ProgressionItem.FloorClear || beforeDeparture && decision.Item == ProgressionItem.Flight);
    }

    private unsafe ProgressionItemStock ReadProgressionStock(InstanceContentDeepDungeon* dd, uint slot, bool active = false) =>
        new(_pomanderManager.GetCount(slot), FloorItemExecutor.CanUsePomanderOnFloor(slot, dd) &&
            IsPomanderAvailableForFloorUse(slot), active);

    private unsafe bool TryUseProgressionFloorClear(InstanceContentDeepDungeon* dd, byte stone, string reason)
    {
        var purpose = DungeonId == 2 ? FloorItemUsePurpose.FloorClear :
            stone == 1 ? FloorItemUsePurpose.NaturalPoisonfruit : FloorItemUsePurpose.NaturalPassageMazeroot;
        if (!TryDispatchFloorStone(stone, dd, reason, purpose)) return false;
        _magiciteAwaitingRelease = true;
        return true;
    }

    private unsafe void RecordProgressionItemDecision(InstanceContentDeepDungeon* dd,
        in ProgressionItemSnapshot snapshot, in ProgressionItemDecision decision, byte clearStone, string outcome)
    {
        var trace = (snapshot, decision, dd->DeepDungeonStatusId, dd->DeepDungeonBanId, dd->DeepDungeonDangerId, clearStone, outcome);
        if (_lastProgressionItemTrace == trace && outcome != "dispatched") return;
        _lastProgressionItemTrace = trace;
        RecordReplayEvent("progression-item-decision", new
        {
            floor = Floor,
            dungeonId = DungeonId,
            phase = _phase.ToString(),
            stage = snapshot.Stage.ToString(),
            pressure = snapshot.Pressure.ToString(),
            statusId = dd->DeepDungeonStatusId,
            banId = dd->DeepDungeonBanId,
            dangerId = dd->DeepDungeonDangerId,
            snapshot.RemainingMobFloors,
            snapshot.PassageOpen,
            snapshot.CurrentFloorItemUsed,
            snapshot.NextFloorIsMob,
            snapshot.NextFloorIsResult,
            stocks = new { snapshot.FloorClear, snapshot.Serenity, snapshot.Frailty, snapshot.Flight, snapshot.Strength, snapshot.Haste },
            clearStone,
            item = decision.Item.ToString(),
            reason = decision.Reason,
            outcome
        });
    }
}
