using System;
using System.Numerics;
using MasterPlayer.MechanicRuntime;
using DeepDungeon.Fsd.Core;

namespace DeepDungeon.Fsd.Dalamud.Coord;

/// <summary>
/// Bounded navigation primitive: every floor traversal funnels through here so
/// pending-hangs, off-mesh destinations, and unreachable targets share one
/// retry/reposition/terminal policy instead of per-call-site watchdogs.
/// </summary>
internal sealed class NavigateOp : FloorOperation
{
    private Vector3 _destination;
    private readonly Func<Vector3?> _repositionAnchor;
    private DateTime _repositionUntilUtc = DateTime.MinValue;

    /// <param name="repositionAnchor">
    /// Optional fallback anchor (e.g. room center) driven to for a window when
    /// the primary destination keeps failing — recovers from mesh islands.
    /// </param>
    internal NavigateOp(
        Vector3 destination,
        int priority = 10,
        int maxAttempts = 6,
        float arrivalRadius = NavigationRange.MovementTolerance,
        Func<Vector3?>? repositionAnchor = null)
        : base(FloorObjectiveKind.None, priority, maxAttempts)
    {
        _destination = destination;
        ArrivalRadius = arrivalRadius;
        _repositionAnchor = repositionAnchor ?? (() => null);
    }

    internal float ArrivalRadius { get; private set; }
    internal Vector3 Destination => _destination;
    internal NavigationPath? PreparedPath { get; set; }

    /// <summary>Start a different goal leg or continue observing the same one.
    /// An unchanged destination retains its attempt and stall evidence.
    /// </summary>
    internal void Retarget(Vector3 destination, float arrivalRadius)
    {
        if (_destination == destination && ArrivalRadius == arrivalRadius)
            return;
        _destination = destination;
        ArrivalRadius = arrivalRadius;
        PreparedPath = null;
        _bestRemaining = float.PositiveInfinity;
        _heldSec = 0f;
        Failure = null;
        Attempts = 0;
        _repositionUntilUtc = DateTime.MinValue;
        Claim = MoveClaim.None;
    }

    private static readonly TimeSpan RepositionWindow = TimeSpan.FromSeconds(6);
    private static readonly NavigationRange RepositionRange = new(2.5f);

    internal override OpStatus Tick(in FloorFacts facts, INativeBoundary native, DateTime now,
        in OpAuthority authority)
    {
        var pos = facts.PlayerPosition;
        // Physical arrival is checked BEFORE the latched failure —
        // actually reaching the goal satisfies the leg without ever
        // resetting the attempt budget.
        if (new NavigationRange(ArrivalRadius).HasArrived(pos, _destination))
        {
            Claim = MoveClaim.None;
            return OpStatus.Succeeded;
        }
        if (Failure.HasValue)
            return OpStatus.Failed;

        // Reposition window: drive the anchor instead of the dead destination.
        if (_repositionUntilUtc > now)
        {
            var anchor = _repositionAnchor();
            if (anchor is { } a && !RepositionRange.HasArrived(pos, a))
            {
                Claim = MoveClaim.To(Priority, a, RepositionRange.Radius);
                return OpStatus.Running;
            }
            _repositionUntilUtc = DateTime.MinValue;
        }

        // Bounded in held time, not wall-clock: a claim masked by escape
        // or danger arbitration is not making progress, but it is also
        // not failing — only frames where our claim actually won burn
        // the effort budget.
        if (authority.HeldDt > 0)
        {
            if (native.RemainingDistance(_destination, ArrivalRadius) is { } remaining &&
                remaining < _bestRemaining - NavigationRange.MovementTolerance)
            {
                _bestRemaining = remaining;
                _heldSec = 0f;
            }
            else
                _heldSec += authority.HeldDt;
        }
        if (_heldSec >= NavDeadlineSec)
        {
            _heldSec = 0;
            NotifyNavFailed(now);
            if (Failure.HasValue)
                return OpStatus.Failed;
        }

        Claim = MoveClaim.To(Priority, _destination, ArrivalRadius, PreparedPath);
        return OpStatus.Running;
    }

    /// <summary>
    /// The pipeline calls this when the native layer reports a navigation
    /// failure (pending timeout, pathfind dead end, stuck). Consumes the
    /// budget; arms the reposition window while attempts remain.
    /// </summary>
    internal override void OnNavFailed(DateTime now) => NotifyNavFailed(now);

    private DateTime _lastFailAt = DateTime.MinValue;
    private float _bestRemaining = float.PositiveInfinity;
    private float _heldSec;
    private static readonly TimeSpan FailCooldown = TimeSpan.FromSeconds(1);
    private const float NavDeadlineSec = 30f;

    private void NotifyNavFailed(DateTime now)
    {
        // The native layer can report Failed on consecutive ticks; count
        // distinct failure episodes, not frames.
        if (now - _lastFailAt < FailCooldown)
            return;
        _lastFailAt = now;
        ++Attempts;
        if (Attempts >= MaxAttempts)
        {
            Failure = FailureKind.Unreachable;
            Claim = MoveClaim.None;
            return;
        }
        _repositionUntilUtc = now + RepositionWindow;
    }
}
