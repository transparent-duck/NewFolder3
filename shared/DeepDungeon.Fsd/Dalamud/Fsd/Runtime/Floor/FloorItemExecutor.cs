using DeepDungeon.Fsd.Core;
using DeepDungeon.Fsd.Dalamud.GameState;
using DeepDungeon.Fsd.Dalamud.Runtime.Helpers;
using global::Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.Game.InstanceContent;

namespace DeepDungeon.Fsd.Dalamud.Runtime.Floor;

internal readonly record struct FloorItemUseResolution(
    PendingFloorItemUse Pending, FloorItemUseConfirmationDecisionKind Decision, int CurrentCount);

/// <summary>Owns dispatch, observation, retries, and cancellation for one floor's item requests.</summary>
internal sealed class FloorItemExecutor : IDisposable
{
    private readonly RunContext _ctx;
    private readonly PomanderManager _pomanderManager;
    private readonly ChatWatchers? _chatWatchers;
    private readonly Func<FloorPhase> _getPhase;
    private readonly Action<string, bool> _recordNative;
    private readonly Action<string, object> RecordReplayEvent;
    private FloorPhase _phase => _getPhase();
    private bool _disposed;
    public FloorItemUseSession Session { get; } = new();
    public PendingIntuitionState PendingIntuition { get; } = new();

    public FloorItemExecutor(RunContext context, PomanderManager pomanders, ChatWatchers? watchers,
        Func<FloorPhase> getPhase, Action<string, bool> recordNative, Action<string, object> record)
    {
        _ctx = context;
        _pomanderManager = pomanders;
        _chatWatchers = watchers;
        _getPhase = getPhase;
        _recordNative = recordNative;
        RecordReplayEvent = record;
    }

    private void RecordNativeIntuitionState(string checkpoint, bool force) => _recordNative(checkpoint, force);

    public void Dispose()
    {
        _disposed = true;
        Session.Cancel();
    }
    public bool CanAttemptPomanderUse(bool allowCombat = false) => CanAttemptPomanderUse(this, allowCombat);

    public static bool CanAttemptPomanderUse(FloorItemExecutor? executor, bool allowCombat = false)
    {
        if (executor?.Session.Pending != null ||
            (!allowCombat && Service.Condition[ConditionFlag.InCombat]) ||
            Service.Condition[ConditionFlag.Casting] ||
                    Service.LocalPlayer?.IsCasting == true ||
                    !PomanderManager.CanDispatchItemRequest() ||
            Service.Condition[ConditionFlag.BetweenAreas] ||
            Service.Condition[ConditionFlag.BetweenAreas51])
        {
            return false;
        }

        if (DateTime.UtcNow < executor?.Session.NextUseAtUtc)
            return false;

        return true;
    }

    public bool IsPomanderAvailableForFloorUse(uint slotIndex)
    {
        return !IsPomanderBlockedForFloor(slotIndex) &&
               _pomanderManager.IsUsable(slotIndex);
    }

    public bool IsPomanderBlockedForFloor(uint slotIndex) =>
        Session.IsBlocked(
            new FloorItemUseKey(FloorItemUseKind.Pomander, slotIndex), DateTime.UtcNow) == true;

    public bool IsStoneBlockedForFloor(byte stoneId) =>
        Session.IsBlocked(
            new FloorItemUseKey(FloorItemUseKind.Stone, stoneId), DateTime.UtcNow) == true;

    public int GetStoneCountAvailableForFloorUse(byte stoneId)
    {
        return IsStoneBlockedForFloor(stoneId)
            ? 0
            : _pomanderManager.GetStoneCount(stoneId);
    }

    public unsafe bool TryUsePomander(
        uint slotIndex,
        InstanceContentDeepDungeon* dd,
        string reason,
        FloorItemUsePurpose purpose = FloorItemUsePurpose.Automatic)
    {
        if (_disposed ||
            !CanAttemptPomanderUse(allowCombat: _ctx?.Duty.IsBossFloor == true &&
                        slotIndex is FloorInitPlanner.StrengthPomanderSlotIndex or FloorInitPlanner.SteelPomanderSlotIndex or FloorInitPlanner.HastePomanderSlotIndex) ||
            !DeepDungeonFloorItemUsePolicy.CanUsePomanders(
                dd->DeepDungeonBanId) ||
            !IsPomanderAvailableForFloorUse(slotIndex))
            return false;

        int countBeforeDispatch = _pomanderManager.GetCount(slotIndex);
        long sightLogSequenceBeforeDispatch =
            _chatWatchers?.SightLogSequence ?? 0;
        long mazerootLogSequenceBeforeDispatch =
            _chatWatchers?.MazerootLogSequence ?? 0;
        if (slotIndex == FloorInitPlanner.IntuitionPomanderSlotIndex)
            RecordNativeIntuitionState($"before-intuition-use:{reason}", force: true);

        long intuitionAttemptId = 0;
        long intuitionExpectedAtMilliseconds = 0;
        DateTime intuitionExpectedAtUtc = DateTime.MinValue;
        if (slotIndex == FloorInitPlanner.IntuitionPomanderSlotIndex)
        {
            intuitionExpectedAtMilliseconds = Environment.TickCount64;
            intuitionExpectedAtUtc = DateTime.UtcNow;
            intuitionAttemptId = _chatWatchers?.ExpectIntuitionResult(dd->Floor) ?? 0;
            PendingIntuition.MarkUsed(dd->Floor, intuitionExpectedAtUtc, intuitionAttemptId);
        }

        if (_pomanderManager.Use(slotIndex))
        {
            DateTime dispatchedAtUtc = DateTime.UtcNow;
            bool blocksFloorSetup =
                _phase == FloorPhase.FloorSetup &&
                slotIndex is FloorInitPlanner.IntuitionPomanderSlotIndex or
                    FloorInitPlanner.SightPomanderSlotIndex;
            var pending = Session.Begin(
                new FloorItemUseKey(FloorItemUseKind.Pomander, slotIndex),
                purpose,
                countBeforeDispatch,
                dispatchedAtUtc,
                reason,
                blocksFloorSetup,
                sightLogSequenceBeforeDispatch,
                mazerootLogSequenceBeforeDispatch,
                intuitionAttemptId,
                intuitionExpectedAtMilliseconds);
            if (slotIndex == FloorInitPlanner.IntuitionPomanderSlotIndex)
                _chatWatchers?.MarkIntuitionUsePendingThisFloor();
            Service.Log.Info(
                $"[FloorPhase] Dispatched pomander slot {slotIndex} request ({reason}) on floor {dd->Floor}; awaiting confirmation");
            RecordReplayEvent("floor-item-use-dispatched", new
            {
                floor = dd->Floor,
                kind = pending.Key.Kind.ToString(),
                itemId = pending.Key.ItemId,
                purpose = pending.Purpose.ToString(),
                attempt = pending.AttemptNumber,
                reason,
                countBeforeDispatch,
                intuitionAttemptId
            });
            if (slotIndex == FloorInitPlanner.IntuitionPomanderSlotIndex)
                RecordNativeIntuitionState($"after-intuition-use:{reason}:dispatched", force: true);
            return true;
        }

        if (slotIndex == FloorInitPlanner.IntuitionPomanderSlotIndex)
        {
            PendingIntuition.CancelAttempt(intuitionAttemptId);
            _chatWatchers?.CancelExpectedIntuitionResult(intuitionAttemptId);
            RecordNativeIntuitionState($"after-intuition-use:{reason}:failed", force: true);
        }
        Session.DelayRejectedRequest(DateTime.UtcNow);
        return false;
    }

    public unsafe bool TryDispatchFloorStone(
        byte stoneId,
        InstanceContentDeepDungeon* dd,
        string reason,
        FloorItemUsePurpose purpose)
    {
        if (_disposed ||
            !CanAttemptPomanderUse(allowCombat: purpose == FloorItemUsePurpose.BossSerenity && _ctx?.Duty.IsBossFloor == true) ||
            !DeepDungeonFloorItemUsePolicy.CanUsePtIncense(
                dd->DeepDungeonBanId) ||
            GetStoneCountAvailableForFloorUse(stoneId) <= 0)
        {
            return false;
        }

        int countBeforeDispatch = _pomanderManager.GetStoneCount(stoneId);
        long sightLogSequenceBeforeDispatch =
            _chatWatchers?.SightLogSequence ?? 0;
        long mazerootLogSequenceBeforeDispatch =
            _chatWatchers?.MazerootLogSequence ?? 0;
        if (!_pomanderManager.UseStone(stoneId))
            return false;

        DateTime dispatchedAtUtc = DateTime.UtcNow;
        var pending = Session.Begin(
            new FloorItemUseKey(FloorItemUseKind.Stone, stoneId),
            purpose,
            countBeforeDispatch,
            dispatchedAtUtc,
            reason,
            _phase == FloorPhase.FloorSetup &&
            purpose == FloorItemUsePurpose.NaturalReveal,
            sightLogSequenceBeforeDispatch,
            mazerootLogSequenceBeforeDispatch,
            0,
            0);
        Service.Log.Info(
            $"[FloorPhase] Dispatched PT incense {stoneId} request ({reason}) on floor {dd->Floor}; awaiting confirmation");
        RecordReplayEvent("floor-item-use-dispatched", new
        {
            floor = dd->Floor,
            kind = pending.Key.Kind.ToString(),
            itemId = pending.Key.ItemId,
            purpose = pending.Purpose.ToString(),
            attempt = pending.AttemptNumber,
            reason,
            countBeforeDispatch
        });
        return true;
    }

    public unsafe FloorItemUseResolution? ResolvePending(InstanceContentDeepDungeon* dd)
    {
        var pending = Session.Pending;
        if (pending == null)
            return null;

        int currentCount = pending.Key.Kind == FloorItemUseKind.Pomander
            ? _pomanderManager.GetCount(pending.Key.ItemId)
            : _pomanderManager.GetStoneCount((byte)pending.Key.ItemId);
        bool authoritativeConfirmationObserved =
            pending.AuthoritativeConfirmationObserved ||
            pending.Key is
            {
                Kind: FloorItemUseKind.Pomander,
                ItemId: FloorInitPlanner.SightPomanderSlotIndex
            } &&
            (_chatWatchers?.SightLogSequence ?? 0) >
            pending.SightLogSequenceBeforeDispatch ||
            pending.Key is
            {
                Kind: FloorItemUseKind.Stone,
                ItemId: 2
            } &&
            (_chatWatchers?.MazerootLogSequence ?? 0) >
            pending.MazerootLogSequenceBeforeDispatch;
        var decision = Session.Observe(currentCount, authoritativeConfirmationObserved, DateTime.UtcNow);

        switch (decision)
        {
            case FloorItemUseConfirmationDecisionKind.PendingConfirmation:
                return null;
            case FloorItemUseConfirmationDecisionKind.Confirmed:
                Session.Confirm(pending);
                return new FloorItemUseResolution(pending, decision, currentCount);
            case FloorItemUseConfirmationDecisionKind.WaitingToRetry:
                RecordUnconfirmedFloorItemUse(dd, pending, currentCount, exhausted: false);
                return null;
            case FloorItemUseConfirmationDecisionKind.RetryReady:
                RecordUnconfirmedFloorItemUse(dd, pending, currentCount, exhausted: false);
                CancelPendingIntuitionAttempt(pending);
                Session.ReleaseForRetry(pending);
                return null;
            case FloorItemUseConfirmationDecisionKind.Exhausted:
                RecordUnconfirmedFloorItemUse(dd, pending, currentCount, exhausted: true);
                CancelPendingIntuitionAttempt(pending);
                Session.Exhaust(pending, DateTime.UtcNow);
                return new FloorItemUseResolution(pending, decision, currentCount);
        }
        return null;
    }

    public void CancelPendingIntuitionAttempt(PendingFloorItemUse pending)
    {
        if (pending.Key is not
            {
                Kind: FloorItemUseKind.Pomander,
                ItemId: FloorInitPlanner.IntuitionPomanderSlotIndex
            })
        {
            return;
        }

        PendingIntuition.CancelAttempt(pending.IntuitionAttemptId);
        _chatWatchers?.CancelExpectedIntuitionResult(pending.IntuitionAttemptId);
        _chatWatchers?.CancelPendingIntuitionUseThisFloor();
    }

    private unsafe void RecordUnconfirmedFloorItemUse(
        InstanceContentDeepDungeon* dd,
        PendingFloorItemUse pending,
        int currentCount,
        bool exhausted)
    {
        if (!pending.TryRecordUnconfirmed())
            return;
        Service.Log.Warning(
            $"[FloorPhase] Floor item request was not confirmed: {pending.Key.Kind} {pending.Key.ItemId}, attempt {pending.AttemptNumber}, reason={pending.Reason}, exhausted={exhausted}");
        RecordReplayEvent("floor-item-use-unconfirmed", new
        {
            floor = dd->Floor,
            kind = pending.Key.Kind.ToString(),
            itemId = pending.Key.ItemId,
            purpose = pending.Purpose.ToString(),
            attempt = pending.AttemptNumber,
            countBeforeDispatch = pending.CountBeforeDispatch,
            currentCount,
            exhausted,
            reason = pending.Reason
        });
    }


}
