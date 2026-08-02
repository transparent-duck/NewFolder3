using System;
using System.Numerics;

namespace DeepDungeon.Fsd.Dalamud.Runtime.Floor;

internal sealed class RunWaypointTelemetryTrace
{
    private const double LongSampleGapSeconds = 2d;
    private const double StationaryContaminationSeconds = 1d;
    private const double PositionNoiseEpsilon = 0.001d;
    private DateTime _lastSampleUtc;
    private Vector3 _lastPosition;
    private Vector3 _stationaryAnchor;
    private double _pendingStationarySeconds;
    private bool _stationaryIsUnclassified;

    public RunWaypointTelemetryTrace(
        DateTime startedAtUtc,
        uint jobId,
        uint dungeonId,
        int floorsetStart,
        byte floor,
        long floorGeneration,
        bool controlledSurvey,
        int roomIndex,
        int waypointIndex,
        string executionKind,
        string objectiveType,
        Vector3 from,
        Vector3 target)
    {
        StartedAtUtc = startedAtUtc;
        JobId = jobId;
        DungeonId = dungeonId;
        FloorsetStart = floorsetStart;
        Floor = floor;
        FloorGeneration = floorGeneration;
        ControlledSurvey = controlledSurvey;
        RoomIndex = roomIndex;
        WaypointIndex = waypointIndex;
        ExecutionKind = executionKind;
        ObjectiveType = objectiveType;
        From = from;
        Target = target;
        _lastSampleUtc = startedAtUtc;
        _lastPosition = from;
        _stationaryAnchor = from;
    }

    public DateTime StartedAtUtc { get; }
    public uint JobId { get; }
    public uint DungeonId { get; }
    public int FloorsetStart { get; }
    public byte Floor { get; }
    public long FloorGeneration { get; }
    public bool ControlledSurvey { get; }
    public int RoomIndex { get; }
    public int WaypointIndex { get; }
    public string ExecutionKind { get; }
    public string ObjectiveType { get; }
    public Vector3 From { get; }
    public Vector3 Target { get; }
    public double TravelledDistance { get; private set; }
    public double PureNavigationSeconds { get; private set; }
    public double CombatSeconds { get; private set; }
    public double FixedWaitSeconds { get; private set; }
    public double OtherWaitSeconds { get; private set; }
    public double UnclassifiedSeconds { get; private set; }

    public void Sample(
        DateTime nowUtc,
        Vector3 position,
        TaskPhase phase,
        bool inCombat,
        bool isCasting)
    {
        double elapsed = Math.Max(0d, (nowUtc - _lastSampleUtc).TotalSeconds);
        if (elapsed > LongSampleGapSeconds)
        {
            FlushPendingStationary(asUnclassified: true);
            UnclassifiedSeconds += elapsed;
            ResetStationarySpan(position);
        }
        else
        {
            switch (phase)
            {
                case TaskPhase.Traveling:
                    TravelledDistance += DistanceXz(_lastPosition, position);
                    if (inCombat || isCasting)
                    {
                        FlushPendingStationary(asUnclassified: false);
                        CombatSeconds += elapsed;
                        ResetStationarySpan(position);
                    }
                    else
                    {
                        AccumulateNavigation(elapsed, position);
                    }
                    break;
                case TaskPhase.WaitingPost when string.Equals(ObjectiveType, "Trap", StringComparison.Ordinal):
                    FlushPendingStationary(asUnclassified: false);
                    ResetStationarySpan(position);
                    FixedWaitSeconds += elapsed;
                    break;
                case TaskPhase.WaitingPre:
                case TaskPhase.WaitingPost:
                    FlushPendingStationary(asUnclassified: false);
                    ResetStationarySpan(position);
                    OtherWaitSeconds += elapsed;
                    break;
                default:
                    FlushPendingStationary(asUnclassified: false);
                    ResetStationarySpan(position);
                    UnclassifiedSeconds += elapsed;
                    break;
            }
        }

        _lastSampleUtc = nowUtc;
        _lastPosition = position;
    }

    public RunWaypointTerminalTelemetry Finish(
        DateTime timestampUtc,
        Vector3 finalPosition,
        TaskPhase phase,
        bool inCombat,
        bool isCasting,
        int navigationIssueCount,
        RunWaypointTerminalOutcome outcome,
        string reason)
    {
        Sample(timestampUtc, finalPosition, phase, inCombat, isCasting);
        FlushPendingStationary(asUnclassified: false);
        return new RunWaypointTerminalTelemetry(
            timestampUtc,
            JobId,
            DungeonId,
            FloorsetStart,
            Floor,
            FloorGeneration,
            ControlledSurvey,
            RoomIndex,
            WaypointIndex,
            ExecutionKind,
            ObjectiveType,
            From.X,
            From.Y,
            From.Z,
            Target.X,
            Target.Y,
            Target.Z,
            DistanceXz(From, Target),
            TravelledDistance,
            PureNavigationSeconds,
            CombatSeconds,
            FixedWaitSeconds,
            OtherWaitSeconds,
            UnclassifiedSeconds,
            Math.Max(0, navigationIssueCount),
            outcome,
            reason ?? string.Empty);
    }

    private void AccumulateNavigation(double elapsed, in Vector3 position)
    {
        if (DistanceXz(_stationaryAnchor, position) > PositionNoiseEpsilon)
        {
            FlushPendingStationary(asUnclassified: false);
            PureNavigationSeconds += elapsed;
            ResetStationarySpan(position);
            return;
        }

        if (_stationaryIsUnclassified)
        {
            UnclassifiedSeconds += elapsed;
            return;
        }

        _pendingStationarySeconds += elapsed;
        if (_pendingStationarySeconds >= StationaryContaminationSeconds)
        {
            FlushPendingStationary(asUnclassified: true);
            _stationaryIsUnclassified = true;
        }
    }

    private void FlushPendingStationary(bool asUnclassified)
    {
        if (_pendingStationarySeconds <= 0d)
            return;

        if (asUnclassified)
            UnclassifiedSeconds += _pendingStationarySeconds;
        else
            PureNavigationSeconds += _pendingStationarySeconds;
        _pendingStationarySeconds = 0d;
    }

    private void ResetStationarySpan(in Vector3 position)
    {
        _stationaryAnchor = position;
        _pendingStationarySeconds = 0d;
        _stationaryIsUnclassified = false;
    }

    private static double DistanceXz(in Vector3 left, in Vector3 right)
    {
        double dx = left.X - right.X;
        double dz = left.Z - right.Z;
        return Math.Sqrt(dx * dx + dz * dz);
    }
}
