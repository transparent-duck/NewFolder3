using System;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Threading;
using BossMod;
using DeepDungeon.Fsd.Dalamud.Utilities;
using MasterPlayer.MechanicRuntime;

namespace DeepDungeon.Fsd.Dalamud.Coord;

internal sealed partial class NavigationSurface
{
    private sealed record PlanCompletion(EncounterRoute Route, DateTime StartedUtc,
        DateTime CompletedUtc, double ComputeMilliseconds, INavigationGrid? Terrain,
        IReadOnlyList<Vector3> RawPath, PlannerDiagnostics Diagnostics);

    private readonly record struct PlannerDiagnostics(string Solver, string Work, int SearchEvaluations,
        int SearchExtensions, string? SearchExit, double FirstWaveSeconds, double SecondWaveSeconds,
        int IntervalExpanded, bool IntervalBudgetReached, int CandidateCount)
    {
        public PlannerDiagnostics(NavigationPlanner planner) : this(planner.Solver, planner.Work,
            planner.SearchEvaluations, planner.SearchExtensions, planner.SearchExit, planner.FirstWaveSeconds,
            planner.SecondWaveSeconds, planner.IntervalExpanded, planner.IntervalBudgetReached, planner.CandidateCount) { }
    }

    private static readonly Guid PlannerBuild = typeof(NavigationPlanner).Module.ModuleVersionId;
    private static readonly TimeSpan DiagnosticInterval = TimeSpan.FromMilliseconds(200);
    private long _planSequence, _adoptedPlanId, _issuedPlanId;
    private EncounterPlanningSnapshot? _diagnosticSnapshot;
    private DateTime _diagnosticAtUtc, _positionSampleAtUtc;
    private Vector3 _diagnosticPosition, _diagnosticGoal, _positionSample;
    private float _diagnosticSpeed, _diagnosticRadius;
    private long _nativeWrites, _sampledNativeWrites, _nativeWritePlanId;
    private DateTime _nativeWriteAtUtc;
    private Vector2 _nativeAppliedInput, _nativeUpstreamInput;
    private Vector3 _nativeWriteDestination;

    private void CaptureDiagnosticContext(DateTime now, Vector3 destination, Vector3 position,
        float radius, float speed)
    {
        _diagnosticAtUtc = now;
        _diagnosticGoal = destination;
        _diagnosticPosition = position;
        _diagnosticRadius = radius;
        _diagnosticSpeed = speed;
        _diagnosticSnapshot = null;
    }

    // Called after an actual native ordinary-input write. No serialization,
    // file I/O, world scan, or additional native read occurs in this callback.
    private void OnNativeWrite()
    {
        ++_nativeWrites;
        _nativeWriteAtUtc = DateTime.UtcNow;
        _nativeWritePlanId = _issuedPlanId;
        _nativeAppliedInput = _direct.LastAppliedInput;
        _nativeUpstreamInput = _direct.LastUserInput;
        _nativeWriteDestination = _direct.DesiredPosition;
    }

    private static PlanCompletion MeasurePlan(NavigationPlanner planner, NavigationRequest request, CancellationToken token)
    {
        var started = DateTime.UtcNow;
        var stamp = Stopwatch.GetTimestamp();
        var route = planner.Plan(in request, token);
        var elapsed = Stopwatch.GetElapsedTime(stamp).TotalMilliseconds;
        return new(route, started, DateTime.UtcNow, elapsed, planner.CurrentTerrain,
            request.Region != null ? planner.RawPath : planner.RawPath.Count == 0 ? [] : planner.RawPath.ToArray(),
            new(planner));
    }

    private void RecordPlan(long id, int generation, in NavigationRequest request, Vector2 previousDirection,
        PlanCompletion? completion, string decision, DateTime observedAt)
    {
        if (request.Encounter is { } snapshot)
        {
            var timed = new EncounterRouteRequest(snapshot, request.Now, request.PlayerId, request.Position,
                request.Goal, request.GoalRadius, request.Speed, previousDirection) { Observation = request.Observation };
            _record("coord-encounter-plan", BuildPlanRecord(id, generation, _generation,
                timed, completion, decision, observedAt, null));
        }
        else if (request.Region == null && (decision != "adopted" || completion?.Diagnostics.Work != "reuse"))
        {
            _record("coord-spatial-plan", new
            {
                planId = id, generation, decision, observedAtUtc = observedAt, requestedAtUtc = request.Now,
                startedUtc = completion?.StartedUtc, completedUtc = completion?.CompletedUtc,
                computeMs = completion?.ComputeMilliseconds, reason = completion?.Route.Reason,
                attackApproachTarget = request.AttackApproachTarget == 0 ? (ulong?)null : request.AttackApproachTarget,
                start = Position(request.Position), goal = Position(request.Goal),
            });
        }
    }

    // Explicit coordinates are intentional: the run recorder does not enable
    // IncludeFields, so serializing Vector2/WPos directly would lose coordinates.
    private static object Position(Vector2 p) => new { x = Number(p.X), z = Number(p.Y) };
    private static object Position(Vector3 p) => new { x = Number(p.X), y = Number(p.Y), z = Number(p.Z) };
    private static object Position(WPos p) => new { x = Number(p.X), z = Number(p.Z) };
    private static object Input(Vector2 p) => new { left = Number(p.X), forward = Number(p.Y) };
    // Null denotes a non-finite diagnostic number, never a substituted zero.
    private static float? Number(float value) => float.IsFinite(value) ? value : null;
    private static double? Number(double value) => double.IsFinite(value) ? value : null;

    private static object? RegionSearch(NavigationRegionGoal region, DateTime now)
    {
        var s = region.SearchProgress;
        if (s.Id == 0) return null;
        return new
        {
            searchId = s.Id, startedUtc = s.StartedUtc, searchTarget = Position(s.Target),
            elapsedMs = region.Searching ? (double?)(now - s.StartedUtc).TotalMilliseconds : null,
            slices = s.Slices, computeMs = s.ComputeMs, expanded = s.Expanded,
            projections = s.Projections, missingGround = s.MissingGround, status = s.Status.ToString(), s.Failure,
        };
    }

    private static object? Displacement(NativeDisplacementObservation? value) => value is { } motion ? new
    {
        target = Position(motion.Target), motion.ObservedAt, motion.ReceivedAt,
        motion.MovementStart, end = motion.End, duration = Number(motion.Duration),
        progress = Number(motion.Progress), remainingDistance = Number(motion.RemainingDistance),
        motion.Native, motion.ActionId, motion.GlobalSequence,
    } : null;

    private static object? Geometry(EncounterHazardGeometry? geometry) => geometry is { } g ? new
    {
        kind = g.Kind,
        origin = new { x = Number(g.OriginX), z = Number(g.OriginZ) },
        rotationRadians = Number(g.RotationRadians),
        innerRadius = Number(g.InnerRadius),
        outerRadius = Number(g.OuterRadius),
        lengthFront = Number(g.LengthFront),
        lengthBack = Number(g.LengthBack),
        halfWidth = Number(g.HalfWidth),
        halfAngleRadians = Number(g.HalfAngleRadians),
        inverted = g.Inverted,
    } : null;

    private object BuildPlanRecord(long planId, int requestGeneration, int observedGeneration,
        EncounterRouteRequest request, PlanCompletion? completion, string decision,
        DateTime observedAtUtc, string? error)
    {
        var s = request.Snapshot;
        var diagnostics = completion?.Diagnostics ?? default;
        var player = new WPos(request.Position.X, request.Position.Y);
        var route = completion?.Route;
        object? firstMove = null;
        if (route != null)
        {
            for (var i = 1; i < route.Points.Length; ++i)
            {
                var from = route.Points[i - 1];
                var to = route.Points[i];
                if (Vector2.DistanceSquared(from.Position, to.Position) < 1e-6f) continue;
                firstMove = new
                {
                    index = i - 1, from = Position(from.Position), to = Position(to.Position),
                    departureSeconds = Number(from.Seconds),
                    departureUtc = route.OriginUtc.AddSeconds(from.Seconds),
                };
                break;
            }
        }
        return new
        {
            schema = 2, planner = "ai-hints-auto", scoreKind = "hint-constraint-violations",
            solver = diagnostics.Solver, work = diagnostics.Work,
            searchEvaluations = diagnostics.SearchEvaluations, searchExtensions = diagnostics.SearchExtensions,
            searchExit = diagnostics.SearchExit, completionSlack = route?.CompletionSlack,
            planId, decision, observedAtUtc, requestGeneration, observedGeneration,
            plannerBuild = PlannerBuild, error,
            firstWaveSeconds = Number(diagnostics.FirstWaveSeconds),
            secondWaveSeconds = Number(diagnostics.SecondWaveSeconds),
            intervalExpanded = diagnostics.IntervalExpanded,
            intervalBudgetReached = diagnostics.IntervalBudgetReached,
            intervalCandidates = diagnostics.CandidateCount,
            request = new
            {
                nowUtc = request.Now, playerId = request.PlayerId,
                position = Position(request.Position), goal = Position(request.Goal),
                goalRadius = Number(request.GoalRadius), speed = Number(request.Speed),
                previousDirection = Position(request.PreviousDirection),
                observedVelocity = Position(request.Velocity),
                acceleration = Number(request.Motion.Acceleration),
                snapshot = new
                {
                    territory = s.Territory, encounter = s.EncounterId, bossOid = s.BossOID,
                    capturedAtUtc = s.CapturedAt,
                    ageAtRequestMs = (request.Now - s.CapturedAt).TotalMilliseconds,
                    bossPosition = Position(s.BossPosition), bossRadius = Number(s.BossRadius),
                    bossTargetable = s.BossTargetable, bossDead = s.BossDead,
                    hintModule = s.Hints?.ModuleName,
                    keepMoving = s.Hints?.KeepMoving,
                    pendingKnockback = s.Hints?.PendingKnockback,
                    displacement = Displacement(request.Displacement),
                    receiptDisplacement = Displacement(s.Displacement),
                    knockbacks = s.Knockbacks.Select(k => new {
                        activationUtc = k.Source.Activation, origin = Position(k.Source.Origin),
                        distance = Number(k.Source.Distance), kind = k.Source.Kind.ToString(),
                        immunityStatusId = k.PathDependentImmunityStatus,
                        actionId = k.Timing.ActionId, effectRowId = k.Timing.EffectRowId,
                        applicationDelaySeconds = Number(k.Timing.ApplicationDelay),
                        movementDurationSeconds = Number(k.Timing.Duration), durationEstimated = k.Timing.DurationEstimated,
                    }).ToArray(),
                    specialMode = s.Hints?.Mode.ToString(),
                    arena = new
                    {
                        type = s.Arena.GetType().Name, center = Position(s.ArenaCenter),
                        radius = Number(s.Arena.Radius),
                        geometrySource = s.Hints?.ModuleName,
                    },
                    hazards = s.Hazards.Select((h, index) => new
                    {
                        index, sourceId = h.SourceId, kind = h.Kind.ToString(), predicted = h.Predicted,
                        activationUtc = h.Activation, expiresAtUtc = h.ExpiresAt,
                        secondsFromRequest = (h.Activation - request.Now).TotalSeconds,
                        distanceType = h.Shape.GetType().Name,
                        geometry = Geometry(h.Geometry),
                        signedDistanceAtPlayer = Number(h.Shape.Distance(player)),
                    }).ToArray(),
                    temporaryObstacles = s.TemporaryObstacles.Select(o => new
                    {
                        distanceType = o.GetType().Name,
                        signedDistanceAtPlayer = Number(o.Distance(player)),
                    }).ToArray(),
                    staticObstacles = s.StaticObstacles.Select(o => new
                    {
                        distanceType = o.GetType().Name,
                        signedDistanceAtPlayer = Number(o.Distance(player)),
                    }).ToArray(),
                    movingObstacles = s.MovingObstacles.Select(o => new
                    {
                        sourceId = o.SourceId, position = Position(o.Position),
                        orbitCenter = Position(o.OrbitCenter), radius = Number(o.Radius),
                        angularVelocityRadiansPerSecond = Number(o.AngularVelocity),
                    }).ToArray(),
                    chasers = s.Chasers.Select(c => new
                    {
                        targetId = c.TargetId, targetPosition = Position(c.TargetPosition),
                        previousPosition = Position(c.PreviousPosition), radius = Number(c.Radius),
                        nextStepDistance = Number(c.NextStepDistance), stepDistance = Number(c.StepDistance),
                        remaining = c.Remaining, nextActivationUtc = c.NextActivation,
                        intervalSeconds = Number(c.Interval),
                        adapted = s.ChasingHazards.Contains(c),
                    }).ToArray(),
                    residence = s.Residence is not { } residence ? null : new
                    {
                        statusId = residence.StatusId, initialDeadlineUtc = residence.InitialDeadline,
                        reentrySeconds = residence.ReentrySeconds, safetyMarginSeconds = residence.SafetyMarginSeconds,
                        maximumPlanningSpeed = Number(residence.MaximumPlanningSpeed),
                        statusPresent = residence.StatusPresent, windImmune = residence.WindImmune,
                        requiresImmunityAtUtc = residence.RequiresImmunityAt,
                        safeContactSeconds = Number(residence.InitialSafeContact),
                        immunityContactSeconds = Number(residence.InitialImmunityContact),
                        distanceType = residence.UnsafeRegion.GetType().Name,
                        signedDistanceAtPlayer = Number(residence.UnsafeRegion.Distance(player)),
                    },
                },
            },
            timing = completion == null ? null : new
            {
                startedUtc = completion.StartedUtc, completedUtc = completion.CompletedUtc,
                queueMs = (completion.StartedUtc - request.Now).TotalMilliseconds,
                computeMs = Number(completion.ComputeMilliseconds),
                harvestDelayMs = (observedAtUtc - completion.CompletedUtc).TotalMilliseconds,
                ageAtDecisionMs = (observedAtUtc - request.Now).TotalMilliseconds,
            },
            result = route == null ? null : new
            {
                encounter = route.EncounterId, originUtc = route.OriginUtc,
                kind = route.Kind.ToString(), hits = route.PredictedHits,
                exposure = Number(route.Exposure), expanded = route.Expanded, reason = route.Reason,
                firstMove,
                points = route.Points.Select(p => new
                {
                    seconds = Number(p.Seconds), position = Position(p.Position), gameDriven = p.GameDriven,
                }).ToArray(),
            },
        };
    }

    private void RecordDiagnosticState(DateTime now, string state)
    {
        if (state == _lastDiagState && now - _lastDiag < DiagnosticInterval) return;
        _lastDiagState = state;
        _lastDiag = now;
        var s = _diagnosticSnapshot;
        var r = _route;
        var player = new WPos(_diagnosticPosition.X, _diagnosticPosition.Z);
        var newPositionSample = _diagnosticAtUtc > _positionSampleAtUtc;
        var hasPreviousPosition = _positionSampleAtUtc != default && newPositionSample;
        object? leg = null;
        if (r != null && _routeIndex + 1 < r.Points.Length)
        {
            var a = r.Points[_routeIndex];
            var b = r.Points[_routeIndex + 1];
            leg = new
            {
                from = Position(a.Position), to = Position(b.Position),
                fromSeconds = Number(a.Seconds), toSeconds = Number(b.Seconds),
                waitingSegment = Vector2.DistanceSquared(a.Position, b.Position) < 1e-6f,
            };
        }
        _record("coord-encounter", new
        {
            schema = 2, planner = "ai-hints-auto", scoreKind = "hint-constraint-violations", observedAtUtc = now,
            solver = Solver, work = _planDiagnostics.Work,
            encounter = s?.EncounterId ?? _snapshot?.EncounterId ?? 0, status = state,
            planId = _adoptedPlanId, pendingPlanId = _pendingPlan?.Id,
            snapshotPresent = s != null, snapshotCapturedAtUtc = s?.CapturedAt,
            snapshotAgeMs = s == null ? (double?)null : (now - s.CapturedAt).TotalMilliseconds,
            hazards = s?.Hazards.Length ?? 0, moving = s?.MovingObstacles.Length ?? 0,
            chasers = s?.Chasers.Length ?? 0,
            currentHazards = s?.Hazards.Select((h, index) => new
            {
                index, sourceId = h.SourceId, activationUtc = h.Activation, expiresAtUtc = h.ExpiresAt,
                secondsFromNow = (h.Activation - now).TotalSeconds,
                signedDistanceAtPlayer = Number(h.Shape.Distance(player)),
            }).ToArray(),
            kind = r?.Kind.ToString() ?? "none", hits = r?.PredictedHits,
            exposure = r == null ? (float?)null : Number(r.Exposure),
            expanded = r?.Expanded, step = r == null ? (int?)null : _routeIndex,
            routeOriginUtc = r?.OriginUtc,
            routeElapsedSeconds = r == null ? (double?)null : (now - r.OriginUtc).TotalSeconds,
            attack = _attackRegion is { TargetId: not 0 } attack ? new
            {
                targetId = attack.TargetId, target = Position(attack.Target), holding = attack.Holding,
                searching = attack.Searching, stand = attack.Path is { } path ? Position(path.Points[^1]) : null,
                search = RegionSearch(attack, now),
                probes = new { outOfRange = _attackOutOfRange, blocked = _attackSightBlocked,
                    unknown = _attackSightUnknown, clear = _attackStandClear },
            } : null,
            leg, failure = _failure,
            position = Position(_diagnosticPosition), positionObservedAtUtc = _diagnosticAtUtc,
            observedDisplacementXZ = hasPreviousPosition ? Number(Vector2.Distance(
                new(_positionSample.X, _positionSample.Z), new(_diagnosticPosition.X, _diagnosticPosition.Z))) : null,
            observedDisplacementSeconds = hasPreviousPosition
                ? (double?)(_diagnosticAtUtc - _positionSampleAtUtc).TotalSeconds : null,
            goal = Position(_diagnosticGoal), goalRadius = Number(_diagnosticRadius), speed = Number(_diagnosticSpeed),
            observedVelocity = Position(_observed.Velocity),
            movementModel = new { acceleration = Number(_observed.Motion.Acceleration) },
            displacementVelocity = Position(_observed.DisplacementVelocity),
            forcedMovement = _observed.ForcedMovement, movementLocked = _observed.MovementLocked,
            pendingDisplacement = _observed.PendingDisplacement,
            displacement = Displacement(_observed.Displacement ?? s?.Displacement),
            control = new
            {
                directEnabled = _direct.Enabled, activeOwner = DirectMovementController.ActiveOwner,
                inputHooksReady = DirectMovementController.InputHooksReady,
                initializationFailure = DirectMovementController.InitializationFailure,
                ownerControlsAvailable = _direct.ControlsAvailable,
                hookLastSampleAgeMs = _direct.LastInputTimestamp == 0 ? (double?)null
                    : ((double)Stopwatch.GetTimestamp() / Stopwatch.Frequency - _direct.LastInputTimestamp) * 1000,
                hookLastSampleUpstreamInput = _direct.LastInputTimestamp == 0 ? null : Input(_direct.LastUserInput),
                issuedPlanId = _issuedPlanId,
                requestedDestination = _direct.Enabled ? Position(_direct.DesiredPosition) : null,
                nativeWriteCount = _nativeWrites,
                nativeWritesSincePreviousSample = _nativeWrites - _sampledNativeWrites,
                lastNativeWrite = _nativeWrites == 0 ? null : new
                {
                    atUtc = _nativeWriteAtUtc, planId = _nativeWritePlanId,
                    ageMs = (now - _nativeWriteAtUtc).TotalMilliseconds,
                    destination = Position(_nativeWriteDestination),
                    appliedInput = Input(_nativeAppliedInput), upstreamInput = Input(_nativeUpstreamInput),
                },
            },
        });
        _sampledNativeWrites = _nativeWrites;
        if (newPositionSample)
        {
            _positionSampleAtUtc = _diagnosticAtUtc;
            _positionSample = _diagnosticPosition;
        }
    }
}
