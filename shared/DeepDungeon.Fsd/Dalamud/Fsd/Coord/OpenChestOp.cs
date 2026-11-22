using System;
using DeepDungeon.Fsd.Core;
using DeepDungeon.Fsd.Dalamud.Runtime;
using DeepDungeon.Fsd.Dalamud.Runtime.Search;
using MasterPlayer.MechanicRuntime;

namespace DeepDungeon.Fsd.Dalamud.Coord;

/// <summary>Navigation owns the interaction region. The op owns the native
/// chest attempt, item policy, waits and server acceptance.</summary>
internal sealed class OpenChestOp : FloorOperation
{
    private const int OpPriority = 10;
    private readonly ChestInteractionTarget _waypoint;
    private readonly ChestInteractionAttempt _attempt;
    private readonly NavigateOp _waypointNav;
    private ChestTargetVerb? _arrived;
    private DateTime _preWaitSince, _openWaitSince, _awaySince, _lastFailure;
    private float _approachSeconds;

    internal OpenChestOp(ChestInteractionTarget waypoint, SiteWorkToken token = default, int maxAttempts = 6,
        ChestInteractionAttempt? overcap = null)
        : base(FloorObjectiveKind.OpenVisibleBandedChest, OpPriority, maxAttempts)
    {
        _waypoint = waypoint;
        Token = token;
        _attempt = new ChestInteractionAttempt(waypoint)
        {
            PendingGoldOvercapSlotIndex = overcap?.PendingGoldOvercapSlotIndex,
            PendingSilverOvercapStoneRowId = overcap?.PendingSilverOvercapStoneRowId,
        };
        _waypointNav = new NavigateOp(waypoint.Position, OpPriority, maxAttempts);
    }

    internal SiteWorkToken Token { get; }
    internal ChestInteractionTarget Waypoint => _waypoint;
    internal override ChestInteractionAttempt? ActiveChestAttempt => _attempt;
    internal NavigationRegionExhaustion? Exhaustion { get; private set; }
    internal override bool ProtectsCommit => Failure == null &&
        (_attempt.InteractionStartedAtUtc != default || _preWaitSince != default || _openWaitSince != default);

    internal override void OnNavArrived() => _arrived = Claim.Verb as ChestTargetVerb?;
    internal override void OnPreempted() => _arrived = null;
    internal void OnNavUnreachable(NavigationRegionExhaustion exhaustion)
    {
        Exhaustion = exhaustion;
        Failure = FailureKind.Unreachable;
    }

    internal override void OnNavFailed(DateTime now)
    {
        if (Claim.Verb is GoToVerb)
        {
            _waypointNav.OnNavFailed(now);
            return;
        }
        if (Claim.Verb is not ChestTargetVerb || now - _lastFailure < TimeSpan.FromSeconds(1)) return;
        _lastFailure = now;
        base.OnNavFailed(now);
    }

    internal override string Describe() => Failure != null ? $"OpenChest(failed:{Failure})" :
        _awaySince != default ? "OpenChest(reapproach)" :
        _openWaitSince != default ? "OpenChest(open-wait)" :
        _preWaitSince != default ? "OpenChest(pre-wait)" : "OpenChest";

    internal override OpStatus Tick(in FloorFacts facts, INativeBoundary native, DateTime now,
        in OpAuthority authority)
    {
        var arrived = _arrived;
        _arrived = null;
        Claim = MoveClaim.None;
        if (native.IsChestAccepted(_attempt, facts.Evidence, out _))
        {
            Failure = null;
            return OpStatus.Succeeded;
        }
        if (Failure.HasValue) return OpStatus.Failed;
        if (facts.PlayerDead || authority.MaskedDt > 0) return OpStatus.Running;

        var obs = native.ObserveChestPosition(_attempt, facts.Evidence);
        if (!obs.Available)
        {
            // An unloaded waypoint has no object identity or sight observation yet.
            var status = _waypointNav.Tick(facts, native, now, authority);
            if ((_openWaitSince != default || status == OpStatus.Succeeded) &&
                native.IsChestTargetMissing(_attempt, facts.Evidence))
            {
                Failure = FailureKind.Blocked;
                return OpStatus.Failed;
            }
            Claim = _waypointNav.Claim;
            if (status == OpStatus.Failed) Failure = _waypointNav.Failure;
            if (OpenTimedOut(native, now)) return OpStatus.Failed;
            return status == OpStatus.Failed ? status : OpStatus.Running;
        }

        var goal = new ChestTargetVerb(obs.GameObjectId, obs.Position, obs.Range);
        Claim = new MoveClaim(Priority, goal);
        // Keep submitting the region during interaction. A new hazard can then
        // choose another safe interaction point, even while we wait for a reply.
        if (arrived != goal || !obs.InRange || obs.SightBlocked)
        {
            if (obs.WaitingForReply && OpenTimedOut(native, now)) return OpStatus.Failed;
            if (!obs.WaitingForReply && _openWaitSince != default && _awaySince == default) _awaySince = now;
            _approachSeconds += authority.HeldDt;
            if (_approachSeconds >= 30)
            {
                _approachSeconds = 0;
                OnNavFailed(now);
            }
            return Failure.HasValue ? OpStatus.Failed : OpStatus.Running;
        }

        _approachSeconds = 0;
        if (_awaySince != default)
        {
            _openWaitSince += now - _awaySince;
            _awaySince = default;
        }
        if (OpenTimedOut(native, now)) return OpStatus.Failed;
        if (_openWaitSince == default)
        {
            if (!native.CanStartChest(_attempt))
            {
                if (_preWaitSince == default) _preWaitSince = now;
                if ((now - _preWaitSince).TotalSeconds >= Runtime.Floor.FloorExplorationController.SilverWaitTimeoutSeconds)
                {
                    Failure = FailureKind.Terminal;
                    return OpStatus.Failed;
                }
                return OpStatus.Running;
            }
            _preWaitSince = default;
            _openWaitSince = now;
        }
        var overcap = native.ResolveChestOvercap(_attempt);
        if (overcap == ChestOvercapProgress.PolicySkipped) return OpStatus.PolicySkipped;
        if (overcap == ChestOvercapProgress.Ready && facts.Evidence != null)
            native.TryChestInteract(_attempt, facts.Evidence, out _, out _);
        return OpStatus.Running;
    }

    private bool OpenTimedOut(INativeBoundary native, DateTime now)
    {
        if (_openWaitSince == default || (now - _openWaitSince).TotalSeconds <= native.ChestOpenTimeoutSeconds(_waypoint))
            return false;
        Failure = FailureKind.Terminal;
        return true;
    }
}
