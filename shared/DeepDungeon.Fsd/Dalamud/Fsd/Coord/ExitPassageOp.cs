using System;
using System.Numerics;
using DeepDungeon.Fsd.Core;

namespace DeepDungeon.Fsd.Dalamud.Coord;

/// <summary>
/// Exit the floor through the passage: approach the projected walking point,
/// wait for the floor transition, and push through when the transition needs the extra step.
/// One NavigateOp instance retargets per stage — the child claim is always
/// the claim the pipeline sees, never overwritten with a different goal.
/// Completion is a floor change / outer floor teardown, never nav arrival.
/// </summary>
internal sealed class ExitPassageOp : FloorOperation
{
    private enum Stage { Approach, WaitForTransition, PushThrough }

    private readonly NavigateOp _nav;
    private readonly int _floor;
    private readonly ulong _actorId;
    private readonly int _passageRoom;
    private readonly Func<Vector3?> _walkingTarget;
    private readonly Func<Vector3?> _actorTarget;
    private Stage _stage = Stage.Approach;
    private float _transitionWaitSeconds;
    private bool _pushCompleted;
    private float _unobservedSeconds;
    private long? _evidenceWhenUnobserved;
    private Vector3 _pushTarget;
    private const int OpPriority = 10;
    private const int MaxNavAttempts = 8;
    private static readonly NavigationRange TriggerRange = new(1.5f);

    internal override string Describe() =>
        $"room={_passageRoom} ph={_stage} nav={_nav.Attempts}/{_nav.MaxAttempts} dest={_nav.Destination} wait={_transitionWaitSeconds:0.0} unobserved={_unobservedSeconds:0.0} pushed={_pushCompleted}";

    internal override bool ProtectsCommit => _stage != Stage.Approach;

    internal ExitPassageOp(
        Vector3 passageDestination, int floor, int passageRoom,
        Func<Vector3?> repositionAnchor, Func<Vector3?> passageTarget,
        Func<Vector3?> passageActorTarget, ulong actorId)
        : base(FloorObjectiveKind.EnterPassage, OpPriority, MaxNavAttempts)
    {
        _nav = new NavigateOp(passageDestination, OpPriority, MaxNavAttempts, NavigationRange.MovementTolerance, repositionAnchor);
        _floor = floor;
        _passageRoom = passageRoom;
        _walkingTarget = passageTarget;
        _actorTarget = passageActorTarget;
        _actorId = actorId;
    }

    internal Vector3 Destination => _nav.Destination;

    internal override OpStatus Tick(in FloorFacts facts, INativeBoundary native, DateTime now,
        in OpAuthority authority)
    {
        if (facts.Floor != _floor)
            return OpStatus.Succeeded;

        var walking = _walkingTarget() ?? _nav.Destination;
        var actor = _actorTarget();
        if (_actorId != 0 && facts.Evidence is { Available: true } evidence)
        {
            foreach (var observed in evidence.PassageActors)
            {
                if (observed.GameObjectId != _actorId) continue;
                if (NativePassageAvailability.IsRemoved(observed.BaseId, observed.SharedTimelineState))
                {
                    Failure = FailureKind.PassageRemoved;
                    return OpStatus.Failed;
                }
                if (!NativePassageAvailability.IsAvailable(observed.BaseId, observed.SharedTimelineState))
                    actor = null;
                break;
            }
        }

        // Native room indices can lag physical entry. They annotate the goal;
        // the planner routes to its coordinates on the current floor bitmap.
        switch (_stage)
        {
            case Stage.Approach:
            {
                // Approach the CURRENT walking point — the child nav claim
                // is the claim the pipeline dispatches.
                _nav.Retarget(walking, NavigationRange.MovementTolerance);
                var status = _nav.Tick(facts, native, now, authority);
                if (status == OpStatus.Failed)
                {
                    Failure = _nav.Failure;
                    return OpStatus.Failed;
                }
                Claim = _nav.Claim;
                if (status == OpStatus.Succeeded)
                {
                    _stage = Stage.WaitForTransition;
                }
                return OpStatus.Running;
            }

            case Stage.WaitForTransition:
            {
                // Holding at the trigger — displaced outside the ACTUAL
                // staging location (horizontal distance to the real
                // target) means resume approach, not keep holding.
                var staging = actor ?? walking;
                if (!TriggerRange.Contains(facts.PlayerPosition, staging))
                {
                    _stage = Stage.Approach;
                    _transitionWaitSeconds = 0;
                    goto case Stage.Approach;
                }
                Claim = MoveClaim.Hold(Priority);
                // An absent or inactive beacon cannot spend contact time.
                // A sustained valid observation at the trigger can release
                // this attempt while keeping the observed contact duration.
                if (!actor.HasValue)
                {
                    if (facts.Evidence is not { Available: true } current)
                        return OpStatus.Running;
                    _evidenceWhenUnobserved ??= current.RefreshSequence;
                    if (!facts.InCombat && !facts.PlayerDead) _unobservedSeconds += authority.HeldDt;
                    if (_unobservedSeconds <= 8f || current.RefreshSequence <= _evidenceWhenUnobserved.Value)
                        return OpStatus.Running;
                    Failure = FailureKind.PassageUnobserved;
                    return OpStatus.Failed;
                }
                _unobservedSeconds = 0;
                _evidenceWhenUnobserved = null;
                // Only our held wait spends the transition budget. Suspension,
                // combat and a competing movement owner cannot falsify a beacon.
                if (!facts.InCombat && !facts.PlayerDead)
                    _transitionWaitSeconds += authority.HeldDt;
                if (_transitionWaitSeconds > 8f)
                {
                    if (_pushCompleted)
                    {
                        // Arrival and one push did not transfer this attempt.
                        // The existing floor-scoped work ledger can try another
                        // actor; this is not a permanent "fake beacon" label.
                        Failure = FailureKind.PassageNoTransfer;
                        return OpStatus.Failed;
                    }
                    // Push-through target fixed ONCE on entering the stage:
                    // the existing 1.5m direction off the REAL actor, on
                    // the walking point's elevation — the raw actor's Y
                    // can sit above the projected floor and would make an
                    // unreachable raised goal.
                    var trigger = actor.Value;
                    var dir = new Vector3(
                        trigger.X - facts.PlayerPosition.X, 0f,
                        trigger.Z - facts.PlayerPosition.Z);
                    if (dir.LengthSquared() < 0.25f)
                        dir = new Vector3(0, 0, 1.5f);
                    else
                        dir = Vector3.Normalize(dir) * 1.5f;
                    _pushTarget = new Vector3(trigger.X, walking.Y, trigger.Z) + dir;
                    _stage = Stage.PushThrough;
                }
                return OpStatus.Running;
            }

            case Stage.PushThrough:
            {
                _nav.Retarget(_pushTarget, NavigationRange.MovementTolerance);
                var status = _nav.Tick(facts, native, now, authority);
                if (status == OpStatus.Failed)
                {
                    Failure = _nav.Failure;
                    return OpStatus.Failed;
                }
                Claim = _nav.Claim;
                if (status == OpStatus.Succeeded)
                {
                    // Reapproach once and observe a final transition wait.
                    _stage = Stage.Approach;
                    _transitionWaitSeconds = 0;
                    _pushCompleted = true;
                }
                return OpStatus.Running;
            }
        }
        return OpStatus.Running;
    }

    internal override void OnNavFailed(DateTime now) => _nav.OnNavFailed(now);
}
