using DeepDungeon.Fsd.Core;

namespace DeepDungeon.Fsd.Runtime;

public enum FloorItemUseKind { Pomander, Stone }

public enum FloorItemUsePurpose
{
    Automatic, GoldChestOvercap, SilverChestOvercap, ControlledStrength,
    NaturalReveal, ControlledReveal, NaturalPoisonfruit, NaturalPassageMazeroot,
    ControlledPoisonfruit, BossSerenity
}

public readonly record struct FloorItemUseKey(FloorItemUseKind Kind, uint ItemId);

public sealed class PendingFloorItemUse
{
    public required FloorItemUseKey Key { get; init; }
    public required FloorItemUsePurpose Purpose { get; init; }
    public required int CountBeforeDispatch { get; init; }
    public required int AttemptNumber { get; init; }
    public required DateTime DispatchedAtUtc { get; init; }
    public required string Reason { get; init; }
    public bool BlocksFloorSetup { get; init; }
    public long SightLogSequenceBeforeDispatch { get; init; }
    public long MazerootLogSequenceBeforeDispatch { get; init; }
    public long IntuitionAttemptId { get; init; }
    public long IntuitionExpectedAtMilliseconds { get; init; }
    public bool AuthoritativeConfirmationObserved { get; private set; }
    private bool _unconfirmedRecorded;

    public void ObserveAuthoritativeConfirmation() => AuthoritativeConfirmationObserved = true;

    public bool TryRecordUnconfirmed()
    {
        if (_unconfirmedRecorded) return false;
        _unconfirmedRecorded = true;
        return true;
    }
}

/// <summary>
/// Owns the single item request channel for one stable floor. Native dispatch and
/// evidence acquisition remain in the game adapter; the caller supplies their results.
/// Timing and attempt limits are implementation policy, not a game-mechanic claim.
/// Exhausted batches defer only their item key before another bounded batch may begin.
/// </summary>
public sealed class FloorItemUseSession
{
    private readonly Dictionary<FloorItemUseKey, int> _unconfirmedAttempts = new();
    private readonly Dictionary<FloorItemUseKey, DateTime> _deferredItemsUntilUtc = new();
    public PendingFloorItemUse? Pending { get; private set; }
    public DateTime NextUseAtUtc { get; private set; } = DateTime.MinValue;
    public bool DispatchedThisUpdate { get; private set; }

    public void BeginUpdate() => DispatchedThisUpdate = false;
    public bool IsBlocked(FloorItemUseKey key, DateTime now)
    {
        if (!_deferredItemsUntilUtc.TryGetValue(key, out var until)) return false;
        if (now < until) return true;
        _deferredItemsUntilUtc.Remove(key);
        _unconfirmedAttempts.Remove(key);
        return false;
    }
    public void DelayRejectedRequest(DateTime now) => NextUseAtUtc = now.AddSeconds(1);

    public PendingFloorItemUse Begin(
        FloorItemUseKey key, FloorItemUsePurpose purpose, int countBeforeDispatch,
        DateTime dispatchedAtUtc, string reason, bool blocksFloorSetup,
        long sightLogSequenceBeforeDispatch, long mazerootLogSequenceBeforeDispatch,
        long intuitionAttemptId, long intuitionExpectedAtMilliseconds)
    {
        int attemptNumber = _unconfirmedAttempts.TryGetValue(key, out int previous) ? previous + 1 : 1;
        _unconfirmedAttempts[key] = attemptNumber;
        Pending = new PendingFloorItemUse
        {
            Key = key, Purpose = purpose, CountBeforeDispatch = countBeforeDispatch,
            AttemptNumber = attemptNumber, DispatchedAtUtc = dispatchedAtUtc, Reason = reason,
            BlocksFloorSetup = blocksFloorSetup, SightLogSequenceBeforeDispatch = sightLogSequenceBeforeDispatch,
            MazerootLogSequenceBeforeDispatch = mazerootLogSequenceBeforeDispatch,
            IntuitionAttemptId = intuitionAttemptId, IntuitionExpectedAtMilliseconds = intuitionExpectedAtMilliseconds
        };
        DispatchedThisUpdate = true;
        NextUseAtUtc = dispatchedAtUtc.AddMilliseconds(FloorItemUseConfirmationPolicy.RetryDelayMilliseconds);
        return Pending;
    }

    public FloorItemUseConfirmationDecisionKind Observe(int currentCount, bool authoritativeConfirmation, DateTime now)
    {
        var pending = Pending ?? throw new InvalidOperationException("No pending floor item request.");
        int elapsed = (int)Math.Clamp((now - pending.DispatchedAtUtc).TotalMilliseconds, 0, int.MaxValue);
        return FloorItemUseConfirmationPolicy.Decide(new(
            pending.CountBeforeDispatch, currentCount,
            pending.AuthoritativeConfirmationObserved || authoritativeConfirmation,
            pending.AttemptNumber, elapsed));
    }

    public void Confirm(PendingFloorItemUse pending)
    {
        if (!ReferenceEquals(Pending, pending)) return;
        _unconfirmedAttempts.Remove(pending.Key);
        Pending = null;
    }

    public void ReleaseForRetry(PendingFloorItemUse pending)
    {
        if (ReferenceEquals(Pending, pending)) Pending = null;
    }

    public void Exhaust(PendingFloorItemUse pending, DateTime now)
    {
        if (!ReferenceEquals(Pending, pending)) return;
        _deferredItemsUntilUtc[pending.Key] = now.AddMilliseconds(FloorItemUseConfirmationPolicy.RetryDelayMilliseconds);
        Pending = null;
    }

    public void Cancel()
    {
        Pending = null;
        _unconfirmedAttempts.Clear();
        _deferredItemsUntilUtc.Clear();
        NextUseAtUtc = DateTime.MinValue;
        DispatchedThisUpdate = false;
    }
}
