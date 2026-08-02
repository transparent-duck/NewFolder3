using System;
using DeepDungeon.Fsd.Core;
using DeepDungeon.Fsd.Dalamud.Runtime;

namespace DeepDungeon.Fsd.Dalamud.Runtime.Floor;

internal sealed class RunFloorTelemetryTrace
{
    private const double LongSampleGapSeconds = 2d;
    private DateTime _lastSampleUtc;
    private DateTime _lastStableSampleUtc;

    public RunFloorTelemetryTrace(
        DateTime stableStartedAtUtc,
        uint jobId,
        uint dungeonId,
        int floorsetStart,
        byte floor,
        long floorGeneration,
        bool controlledSurvey,
        bool normalMobFloor)
    {
        StableStartedAtUtc = stableStartedAtUtc;
        JobId = jobId;
        DungeonId = dungeonId;
        FloorsetStart = floorsetStart;
        Floor = floor;
        FloorGeneration = floorGeneration;
        ControlledSurvey = controlledSurvey;
        NormalMobFloor = normalMobFloor;
        _lastSampleUtc = stableStartedAtUtc;
        _lastStableSampleUtc = stableStartedAtUtc;
    }

    public DateTime StableStartedAtUtc { get; }
    public uint JobId { get; }
    public uint DungeonId { get; }
    public int FloorsetStart { get; }
    public byte Floor { get; }
    public long FloorGeneration { get; }
    public bool ControlledSurvey { get; }
    public bool NormalMobFloor { get; }
    public bool PassageCommitObserved { get; private set; }
    public bool HoardOrIntelExecutionStarted { get; private set; }
    public int NavigationIssueCount { get; private set; }
    public double UnclassifiedSeconds { get; private set; }

    public void SampleStable(DateTime nowUtc)
    {
        if (PassageCommitObserved)
            return;

        if (nowUtc < _lastSampleUtc)
        {
            UnclassifiedSeconds += (_lastSampleUtc - nowUtc).TotalSeconds;
            return;
        }

        double elapsed = (nowUtc - _lastSampleUtc).TotalSeconds;
        if (elapsed > LongSampleGapSeconds)
            UnclassifiedSeconds += elapsed;
        _lastSampleUtc = nowUtc;
        _lastStableSampleUtc = nowUtc;
    }

    public void ObserveObjectiveStart(
        FloorObjectiveKind kind,
        bool isIntelExecution)
    {
        if (isIntelExecution ||
            kind is FloorObjectiveKind.DiscoverHoard or
                FloorObjectiveKind.CompleteKnownHoard or
                FloorObjectiveKind.OpenVisibleBandedChest)
        {
            HoardOrIntelExecutionStarted = true;
        }
    }

    public void ObserveNavigationIssue(int count = 1)
    {
        if (count > 0)
            NavigationIssueCount = checked(NavigationIssueCount + count);
    }

    public void ObserveChestRecoveryStarted() => ObserveNavigationIssue();

    public void ObserveStalledEngageRecoveryStarted() => ObserveNavigationIssue();

    public void ObservePassageCommit(DateTime committedAtUtc)
    {
        if (committedAtUtc < StableStartedAtUtc)
        {
            UnclassifiedSeconds += (StableStartedAtUtc - committedAtUtc).TotalSeconds;
            return;
        }
        if (committedAtUtc < _lastSampleUtc)
        {
            UnclassifiedSeconds += (_lastSampleUtc - committedAtUtc).TotalSeconds;
            return;
        }
        double elapsed = (committedAtUtc - _lastSampleUtc).TotalSeconds;
        if (elapsed > LongSampleGapSeconds)
            UnclassifiedSeconds += elapsed;

        PassageCommitObserved = true;
        if (committedAtUtc > _lastStableSampleUtc)
            _lastStableSampleUtc = committedAtUtc;
        _lastSampleUtc = committedAtUtc;
    }

    public RunFloorTerminalTelemetry Finish(
        DateTime observedAtUtc,
        bool hoardOpenedThisFloor,
        RunFloorTerminalOutcome outcome,
        string reason)
    {
        double elapsed = Math.Max(
            0d,
            (_lastStableSampleUtc - StableStartedAtUtc).TotalSeconds);
        return new RunFloorTerminalTelemetry(
            observedAtUtc,
            StableStartedAtUtc,
            _lastStableSampleUtc,
            JobId,
            DungeonId,
            FloorsetStart,
            Floor,
            FloorGeneration,
            ControlledSurvey,
            NormalMobFloor,
            PassageCommitObserved,
            HoardOrIntelExecutionStarted,
            hoardOpenedThisFloor,
            NavigationIssueCount,
            UnclassifiedSeconds,
            elapsed,
            outcome,
            reason ?? string.Empty);
    }
}
