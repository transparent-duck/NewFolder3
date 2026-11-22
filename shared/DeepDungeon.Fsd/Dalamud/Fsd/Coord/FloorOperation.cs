using System;
using System.Numerics;
using MasterPlayer.MechanicRuntime;
using DeepDungeon.Fsd.Core;
using DeepDungeon.Fsd.Dalamud.Runtime.Floor;
using DeepDungeon.Fsd.Dalamud.Runtime.Search;

namespace DeepDungeon.Fsd.Dalamud.Coord;

internal enum OpStatus { Running, Succeeded, Failed, Cancelled, PolicySkipped }

/// <summary>Result of the controller-side chest-overcap relief decision:
/// Ready = proceed with interaction dispatch; Waiting = an item relief is
/// pending/in progress, hold; PolicySkipped = existing policy denies the
/// chest this floor — an explicit policy conclusion, not a failure.</summary>
internal enum ChestOvercapProgress { Ready, Waiting, PolicySkipped }

/// <summary>
/// Movement-authority accounting handed to an op each tick, computed by
/// the pipeline after arbitration resolves the previous frame's claims.
/// HeldDt = seconds the op's claim won and was dispatched; MaskedDt =
/// seconds the op produced a claim that lost to another claim. Effort
/// budgets (nav deadlines, pursuit budgets) burn HeldDt only — being
/// masked by escape/danger must not consume an op's own effort budget.
/// Real world deadlines (a chest's despawn window) stay on wall-clock.
/// </summary>
internal readonly record struct OpAuthority(bool Held, float HeldDt, float MaskedDt)
{
    public static readonly OpAuthority None = new(false, 0f, 0f);
}

/// <summary>
/// Typed failure terminal so callers route outcomes instead of retrying
/// forever: Retryable consumes budget, Unreachable/Blocked end the attempt
/// differently, Terminal kills the operation outright.
/// </summary>
internal enum FailureKind { Retryable, Unreachable, Blocked, Terminal, PassageRemoved, PassageUnobserved, PassageNoTransfer }

/// <summary>
/// Movement request an operation hands to the arbiter each tick — an
/// arbitration envelope only. The arbiter reads Priority; the verb type is
/// the intent and stays opaque until the winning boundary dispatches it.
/// RevalidateAt records which facts revision the claim was computed from —
/// diagnostic metadata, never consulted by arbitration.
/// </summary>
public readonly record struct MoveClaim(
    int Priority,
    IClaimVerb? Verb,
    long RevalidateAt = -1)
{
    public static readonly MoveClaim None = new(int.MaxValue, null);
    public static MoveClaim Hold(int priority) => new(priority, new HoldVerb());
    public static MoveClaim To(int priority, Vector3 dest, float radius = NavigationRange.MovementTolerance,
        NavigationPath? path = null) => new(priority, new GoToVerb(dest, radius) { Path = path });
}

/// <summary>Outcome of a navigation request at the native boundary.</summary>
public enum NavOutcome { Moving, Arrived, Pending, Failed, Unreachable }
internal readonly record struct NavResult(NavOutcome Outcome, NavigationRegionExhaustion? Exhaustion = null);

/// <summary>
/// Thin shell over the legacy/native boundary. Operations only see this —
/// they never call Service.*, nav helpers, or game structs directly.
/// </summary>
/// <summary>Result of a domain room finish.</summary>

internal interface INativeBoundary
{
    /// <summary>
    /// The claim-dispatch channel: the winning claim's verb lands here.
    /// Shared verbs are interpreted by the boundary itself; unknown/profile
    /// verbs must return Failed — never silently drop or reinterpret.
    /// </summary>
    NavResult Execute(IClaimVerb? verb);
    NavOutcome Navigate(Vector3 dest, float arrivalRadius);
    void CancelMovement();
    float? RemainingDistance(Vector3 destination, float arrivalRadius) => null;

    /// <summary>Why the surface's last Navigate reported Failed, when the
    /// backend tracks a reason (bitmap: unreachable/retargeted/no-progress/
    /// owned-elsewhere). Diagnostics only — never a control signal.</summary>
    string? LastNavFailure => null;

    // Chest surface — the AR-1.2 verified interaction lifecycle lives here.
    bool IsChestAccepted(ChestInteractionAttempt attempt, FloorObjectEvidenceSnapshot? evidence,
        out ChestLifecycleSnapshot snapshot);
    bool IsChestTargetMissing(ChestInteractionAttempt attempt, FloorObjectEvidenceSnapshot? evidence);
    bool TryChestInteract(ChestInteractionAttempt attempt, FloorObjectEvidenceSnapshot evidence,
        out ChestLifecycleSnapshot snapshot, out bool retry);
    float ChestOpenTimeoutSeconds(ChestInteractionTarget waypoint);

    /// <summary>Existing silver precondition (e.g. player HP threshold)
    /// behind native acceptance — the op waits, the service decides.
    /// Default true so test fakes keep compiling unchanged.</summary>
    bool CanStartChest(ChestInteractionAttempt attempt) => true;
    /// <summary>Native chest position facts for recovery decisions —
    /// the default is appropriate for fakes that don't support it.</summary>
    ChestPositionObservation ObserveChestPosition(
        ChestInteractionAttempt attempt, FloorObjectEvidenceSnapshot? evidence)
        => default;

    /// <summary>Existing overcap relief decision for this chest attempt —
    /// item policy lives on the controller, never in the op. Default Ready
    /// so test fakes keep compiling unchanged.</summary>
    ChestOvercapProgress ResolveChestOvercap(ChestInteractionAttempt attempt)
        => ChestOvercapProgress.Ready;

        /// <summary>Domain search planner surface; null when no executor is live.</summary>
    SiteSearchBoundary? Search { get; }

    /// <summary>Current clearing target identity and observed position.</summary>
    EnemyChaseTarget? ChaseTarget(Vector3 playerPos, bool allowNewTargets);

    (Vector3 Position, float Radius)? SelectEncounterTarget(EncounterPlanningSnapshot snapshot) =>
        snapshot.Hints is { HasTargetDirective: true, AttackTarget: { } target } ? (target.Position, target.Radius) : null;
    float EncounterAttackRange => 3f;
}

/// <summary>Native-side chest position facts for a claim-only op —
/// computed by the boundary so ops never touch Service/BGCollision
/// directly.</summary>
internal readonly record struct ChestPositionObservation(
    bool Available, bool WaitingForReply,
    bool InRange, bool SightBlocked, ulong GameObjectId, Vector3 Position, NavigationRange Range = default);

internal readonly record struct QuotaSearchRoom(int RoomIndex, Vector3 Center);
internal readonly record struct QuotaSearchResult(bool Available, QuotaSearchRoom? Room);

/// <summary>
/// One typed unit of floor work bound to an objective. Lifecycle is owned by
/// the pipeline; retry budgets are per-operation constructor data, not global
/// constants sprinkled through call sites.
/// </summary>
internal abstract class FloorOperation
{
    protected FloorOperation(FloorObjectiveKind objective, int priority, int maxAttempts)
    {
        Objective = objective;
        Priority = priority;
        MaxAttempts = maxAttempts;
    }

    public FloorObjectiveKind Objective { get; }
    public int Priority { get; }
    public int MaxAttempts { get; }
    public int Attempts { get; protected set; }
    public MoveClaim Claim { get; protected set; } = MoveClaim.None;
    public FailureKind? Failure { get; protected set; }

    /// <summary>Compact op state for diagnostics (recorded in coord-tick).</summary>
    internal virtual string Describe() => GetType().Name;

    internal abstract OpStatus Tick(in FloorFacts facts, INativeBoundary native, DateTime now,
        in OpAuthority authority);

    /// <summary>Invoked when the arbiter preempts this operation's claim.</summary>
    internal virtual void OnPreempted() { }
    /// <summary>Suspended while the route head moved elsewhere — releases
    /// the movement claim but keeps all execution state; rebinding the
    /// same token resumes THIS attempt, never a fresh one.</summary>
    internal virtual void OnSuspended() => OnPreempted();
    /// <summary>Rebound after a suspension — ops may refresh transient
    /// state (last-tick timestamps), never reset their work clocks.</summary>
    internal virtual void OnResumed(DateTime now) { }

    /// <summary>True while a meaningful action is in progress whose
    /// interruption would waste real work — a partial/awaited probe dwell
    /// or a dispatched chest interaction. The scheduler preserves the
    /// committed head only while this holds; plain travel does NOT
    /// protect a commit.</summary>
    internal virtual bool ProtectsCommit => false;

    /// <summary>The chest interaction attempt this op currently owns, if
    /// any — read-only exposure so the controller's native overcap/item
    /// channel can bind to the REAL attempt, not a stale sidecar.</summary>
    internal virtual ChestInteractionAttempt? ActiveChestAttempt => null;

    internal virtual void OnNavArrived() { }

    /// <summary>Invoked when this operation's winning claim came back Failed.</summary>
    internal virtual void OnNavFailed(DateTime now)
    {
        ++Attempts;
        if (Attempts >= MaxAttempts)
            Failure = FailureKind.Unreachable;
    }

    /// <summary>Invoked when the pipeline retires this operation (any status).</summary>
    internal virtual void OnRetired() { }
}
