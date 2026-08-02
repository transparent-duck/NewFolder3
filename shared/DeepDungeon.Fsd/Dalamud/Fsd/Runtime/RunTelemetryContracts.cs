using System;
using DeepDungeon.Fsd.Core;

namespace DeepDungeon.Fsd.Dalamud.Runtime;

public enum RunWaypointTerminalOutcome
{
    Completed,
    Aborted,
    Skipped,
    NavigationFailed
}

public readonly record struct RunWaypointTerminalTelemetry(
    DateTime TimestampUtc,
    uint JobId,
    uint DungeonId,
    int FloorsetStart,
    byte Floor,
    long FloorGeneration,
    bool ControlledSurvey,
    int RoomIndex,
    int WaypointIndex,
    string ExecutionKind,
    string ObjectiveType,
    float FromX,
    float FromY,
    float FromZ,
    float TargetX,
    float TargetY,
    float TargetZ,
    double DirectDistance,
    double TravelledDistance,
    double PureNavigationSeconds,
    double CombatSeconds,
    double FixedWaitSeconds,
    double OtherWaitSeconds,
    double UnclassifiedSeconds,
    int NavigationIssueCount,
    RunWaypointTerminalOutcome Outcome,
    string Reason);

public readonly record struct RunFloorBoundaryTelemetry(
    DateTime TimestampUtc,
    uint JobId,
    uint DungeonId,
    int FloorsetStart,
    byte Floor,
    long FloorGeneration,
    bool ControlledSurvey,
    string Reason);

public enum RunFloorTerminalOutcome
{
    PassageCompleted,
    PlayerDeath,
    Aborted
}

/// <summary>
/// One terminal fact for a stable floor runtime. The measured end is the last
/// stable pre-transition sample or passage commit, not the later observer
/// callback time.
/// </summary>
public readonly record struct RunFloorTerminalTelemetry(
    DateTime ObservedAtUtc,
    DateTime StableStartedAtUtc,
    DateTime StableMeasurementEndedAtUtc,
    uint JobId,
    uint DungeonId,
    int FloorsetStart,
    byte Floor,
    long FloorGeneration,
    bool ControlledSurvey,
    bool NormalMobFloor,
    bool PassageCommitObserved,
    bool HoardOrIntelExecutionStarted,
    bool HoardOpenedThisFloor,
    int NavigationIssueCount,
    double UnclassifiedSeconds,
    double StableBaselineSeconds,
    RunFloorTerminalOutcome Outcome,
    string Reason);

public enum RunFloorStateTrigger
{
    StableSetup,
    FactualChanged
}

public enum RunFloorVisibleChestKind
{
    Bronze,
    Silver,
    Gold
}

public readonly record struct RunFloorRoomFact(
    int RoomIndex,
    uint ConnectionFlags,
    RawWorldPosition Center);

public readonly record struct RunFloorRoomEdge(int LeftRoomIndex, int RightRoomIndex);

public readonly record struct RunFloorCandidateFact(
    int RoomIndex,
    RawWorldPosition Position,
    double PosteriorWeight,
    bool DirectSightSuccessor);

public readonly record struct RunFloorVisibleChestFact(
    int RoomIndex,
    RawWorldPosition Position,
    RunFloorVisibleChestKind Kind);

public enum RunFloorRoutePointKind
{
    Origin,
    RetainedChestRoom,
    RetainedVisibleChest,
    Passage
}

/// <summary>
/// One immutable endpoint in the normal floor route after optional hoard work
/// has been removed. These are factual generated-room / visible-object targets;
/// they are not an optimization decision or a VNav polyline.
/// </summary>
public readonly record struct RunFloorRoutePointFact(
    int RoomIndex,
    RawWorldPosition Position,
    RunFloorRoutePointKind Kind);

/// <summary>
/// A neutral, read-only copy of the factual normal-floor state supplied to an
/// optional host observer. It contains no optimization or navigation decision.
/// </summary>
public sealed class RunFloorStateTelemetry
{
    public DateTime TimestampUtc { get; init; }
    public RunFloorStateTrigger Trigger { get; init; }
    public uint JobId { get; init; }
    public uint DungeonId { get; init; }
    public uint TerritoryId { get; init; }
    public int FloorsetStart { get; init; }
    public byte Floor { get; init; }
    public long FloorGeneration { get; init; }
    public bool ControlledSurvey { get; init; }
    public int ActiveLayoutIndex { get; init; }
    public bool DetailedMapActive { get; init; }
    public bool YieldAvailable { get; init; }
    public FloorsetHoardOpportunity FloorsetHoardOpportunity { get; init; }
    public bool HoardOpenedThisFloor { get; init; }
    public string? CatalogReleaseId { get; init; }
    public string? CatalogModelSha256 { get; init; }
    public string? HoardYieldSha256 { get; init; }
    /// <summary>
    /// The release-frozen p(H) estimate for this floor. Null means the validated
    /// yield artifact did not provide an estimate for the actual floor.
    /// </summary>
    public double? HoardExistsProbability { get; init; }
    public int OriginRoomIndex { get; init; }
    public RawWorldPosition Origin { get; init; }
    public RunFloorRoomFact[] Rooms { get; init; } = [];
    public RunFloorRoomEdge[] RoomEdges { get; init; } = [];
    public RunFloorCandidateFact[] Candidates { get; init; } = [];
    public RawWorldPosition[] ObservedSightTraps { get; init; } = [];
    public RawWorldPosition? ExactHoardIndicator { get; init; }
    public RawWorldPosition? VisibleBanded { get; init; }
    public RunFloorVisibleChestFact[] VisibleChests { get; init; } = [];
    public RunFloorRoutePointFact[] RetainedRoute { get; init; } = [];
    public string? RetainedRouteUnavailableReason { get; init; }
}

/// <summary>
/// Optional host-owned sink for low-frequency, terminal run telemetry. The shared
/// engine publishes neutral facts only; hosts decide whether and how to retain them.
/// </summary>
public interface IRunTelemetryObserver
{
    void ObserveWaypointTerminal(in RunWaypointTerminalTelemetry observation);
    void ObserveFloorBoundary(in RunFloorBoundaryTelemetry observation);
    void ObserveFloorTerminal(in RunFloorTerminalTelemetry observation);
    void ObserveFloorState(RunFloorStateTelemetry observation);
}
