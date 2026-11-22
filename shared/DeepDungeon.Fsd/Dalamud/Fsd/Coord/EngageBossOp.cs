using System;
using System.Numerics;
using DeepDungeon.Fsd.Core;
using DeepDungeon.Fsd.Dalamud.Runtime.Floor;
using MasterPlayer.MechanicRuntime;

namespace DeepDungeon.Fsd.Dalamud.Coord;

/// <summary>
/// One encounter's engage work: the claim carries the boss position/range
/// (attack range does NOT cancel the nav request - constraints and combat
/// positioning are planned together). Bound to a real EncounterId; a
/// missing snapshot is the same work, a different identity is new work.
/// </summary>
internal sealed class EngageBossOp : FloorOperation
{
    private readonly ulong _encounterId;
    private EncounterPlanningSnapshot? _snapshot;
    private bool _completedDeath;
    private string _state = "waiting";

    internal EngageBossOp(ulong encounterId)
        : base(FloorObjectiveKind.DefeatBoss, priority: 30, maxAttempts: int.MaxValue)
        => _encounterId = encounterId;

    internal ulong EncounterId => _encounterId;
    internal string State => _state;

    /// <summary>Current snapshot for this identity; a NULL observation
    /// makes the current input immediately unavailable (no 500ms hold).
    /// A confirmed death is preserved separately — it is a final result,
    /// not a stale capture.</summary>
    internal void Observe(EncounterPlanningSnapshot? snapshot)
    {
        if (snapshot == null)
        {
            _snapshot = null;
            return;
        }
        if (snapshot.EncounterId != _encounterId)
            return;
        _snapshot = snapshot;
        if (snapshot.BossDead)
            _completedDeath = true;
    }

    internal override OpStatus Tick(in FloorFacts facts, INativeBoundary native,
        DateTime now, in OpAuthority authority)
    {
        Claim = MoveClaim.None;
        // Confirmed death for our bound identity wins even if capture is old.
        if (_completedDeath
            || (_snapshot is { BossDead: true } dead && dead.EncounterId == _encounterId))
        {
            _state = "completed";
            return OpStatus.Succeeded;
        }
        if (facts.PlayerDead)
        {
            _state = "dead";
            return OpStatus.Cancelled;
        }
        if (Failure != null)
        {
            _state = "failed";
            return OpStatus.Failed;
        }
        if (_snapshot == null || _snapshot.EncounterId != _encounterId ||
            now - _snapshot.CapturedAt > TimeSpan.FromSeconds(0.5))
        {
            _state = "waiting";
            Claim = MoveClaim.Hold(Priority);
            return OpStatus.Running;
        }
        var destination = _snapshot.BossPosition;
        var targetRadius = _snapshot.BossRadius;
        if (native.SelectEncounterTarget(_snapshot) is { } target)
        {
            destination = target.Position;
            targetRadius = target.Radius;
        }
        // Retain the navigation request even when in attack range — the
        // XZ range flag only selects the state label.
        var dx = destination.X - facts.PlayerPosition.X;
        var dz = destination.Z - facts.PlayerPosition.Z;
        var range = targetRadius + native.EncounterAttackRange;
        _state = MathF.Sqrt(dx * dx + dz * dz) <= range ? "engage" : "approach";
        Claim = new(Priority, new GoToVerb(destination, range) { EncounterId = _encounterId });
        return OpStatus.Running;
    }

    internal override void OnNavFailed(DateTime now)
    {
        ++Attempts;
        // A path failure is temporary; the same encounter continues from fresh observations.
        _state = "waiting";
    }

    internal override void OnPreempted()
    {
        Claim = MoveClaim.None;
        _state = "waiting";
    }
    internal override void OnSuspended() => OnPreempted();
    internal override void OnRetired()
    {
        Claim = MoveClaim.None;
        _state = "retired";
    }

    /// <summary>State from REAL facts/results: approach/engage by range,
    /// waiting on data, completed on observed death, failed on nav.</summary>
    internal override string Describe()
    {
        if (Failure != null) return "engage-failed";
        if (_completedDeath) return "engage-completed";
        if (_snapshot == null) return "engage-waiting";
        return _state switch
        {
            "approach" => "engage-approach",
            "engage" => "engage-engage",
            "completed" => "engage-completed",
            "failed" => "engage-failed",
            _ => "engage-waiting",
        };
    }
}

/// <summary>
/// Floor-scoped driver: binds ONE stable EngageBossOp to the real
/// EncounterId and observes status transitions once. It owns this
/// encounter's work lifecycle; geometry belongs to the navigation surface.
/// </summary>
internal sealed class EncounterDriver
{
    private EngageBossOp? _op;
    private ulong _encounterId;
    private OpStatus _lastReported = OpStatus.Running;
    private bool _reported;

    internal EngageBossOp? Current => _op;
    internal ulong EncounterId => _encounterId;
    internal OpStatus LastReported => _lastReported;
    internal bool HasReported => _reported;

    /// <summary>Bind (or retain) the op for the current encounter id; a
    /// different id is NEW work — reports newIdentity so the caller can
    /// cancel stale boundary movement before executing. Missing snapshot
    /// keeps the existing bound op.</summary>
    internal FloorOperation? EnsureOp(EncounterPlanningSnapshot? snapshot,
        out bool newIdentity)
    {
        newIdentity = false;
        if (snapshot == null)
            return _op;
        if (_op != null && _encounterId == snapshot.EncounterId)
            return _op;
        _op?.OnRetired();
        _encounterId = snapshot.EncounterId;
        _op = new EngageBossOp(_encounterId);
        _reported = false;
        newIdentity = true;
        return _op;
    }

    /// <summary>Record the observed outcome once per identity/status
    /// change; returns true when this tick is a new transition.</summary>
    internal bool Observe(OpStatus status)
    {
        if (_reported && status == _lastReported)
            return false;
        _reported = true;
        _lastReported = status;
        return true;
    }

    /// <summary>Retire the bound op and clear identity — pipeline Reset or
    /// floor change calls this before cancelling the boundary.</summary>
    internal void Retire()
    {
        _op?.OnRetired();
        _op = null;
        _encounterId = 0;
        _reported = false;
        _lastReported = OpStatus.Running;
    }
}
