using System;
using System.Numerics;
using global::Dalamud.Game.ClientState.Conditions;
using DeepDungeon.Fsd.Dalamud.Runtime.Search;

namespace DeepDungeon.Fsd.Dalamud.Runtime.Floor;

public sealed partial class FloorPhaseController
{
    private void StartActiveWaypointTelemetry(RoomWaypoint waypoint)
    {
        if (_runTelemetryObserver == null ||
            _floorRuntime is not { IsDisposed: false } runtime ||
            runtime.ActiveExecution == null ||
            Service.LocalPlayer is not { } player)
            return;

        if (runtime.ActiveExecution.WaypointTelemetry != null)
            EndActiveWaypointTelemetry(RunWaypointTerminalOutcome.Aborted, "WaypointReplaced");

        runtime.ActiveExecution.WaypointTelemetry = new RunWaypointTelemetryTrace(
            DateTime.UtcNow,
            player.ClassJob.RowId,
            runtime.DungeonId,
            ((runtime.Floor - 1) / 10) * 10 + 1,
            runtime.Floor,
            runtime.Generation,
            _ctx?.ControlledPtSurvey != null,
            _executor?.RoomContext?.RoomIndex ?? -1,
            _executor?.RoomContext?.CurrentWaypointIndex ?? -1,
            _searchExecutionKind.ToString(),
            waypoint.Type.ToString(),
            player.Position,
            waypoint.Position);
    }

    private void SampleActiveWaypointTelemetry(TaskPhase phase, Vector3 position)
    {
        _floorRuntime?.ActiveExecution?.WaypointTelemetry?.Sample(
            DateTime.UtcNow,
            position,
            phase,
            Service.Condition[ConditionFlag.InCombat],
            Service.Condition[ConditionFlag.Casting]);
    }

    private void EndActiveWaypointTelemetry(
        RunWaypointTerminalOutcome outcome,
        string reason,
        TaskPhase? observedPhase = null)
    {
        var execution = _floorRuntime?.ActiveExecution;
        var trace = execution?.WaypointTelemetry;
        if (execution == null || trace == null)
            return;

        execution.WaypointTelemetry = null;
        Vector3 position = Service.LocalPlayer?.Position ?? trace.From;
        TaskPhase phase = observedPhase ?? execution.TaskRunner.Phase;
        var terminal = trace.Finish(
            DateTime.UtcNow,
            position,
            phase,
            Service.Condition[ConditionFlag.InCombat],
            Service.Condition[ConditionFlag.Casting],
            execution.TaskRunner.NavigationIssueCount,
            outcome,
            reason);
		_floorRuntime?.RunTelemetry?.ObserveNavigationIssue(
			terminal.NavigationIssueCount);
        try
        {
            _runTelemetryObserver?.ObserveWaypointTerminal(terminal);
        }
        catch (Exception ex)
        {
            Service.Log.Error($"[RunTelemetry] Host waypoint observer failed: {ex}");
        }
    }

    private void ObserveFloorTelemetryBoundary(FloorRuntime runtime, string reason)
    {
        if (_runTelemetryObserver == null)
            return;

        try
        {
            _runTelemetryObserver.ObserveFloorBoundary(new RunFloorBoundaryTelemetry(
                DateTime.UtcNow,
                Service.LocalPlayer?.ClassJob.RowId ?? 0,
                runtime.DungeonId,
                ((runtime.Floor - 1) / 10) * 10 + 1,
                runtime.Floor,
                runtime.Generation,
                _ctx?.ControlledPtSurvey != null,
                reason));
        }
        catch (Exception ex)
        {
            Service.Log.Error($"[RunTelemetry] Host floor observer failed: {ex}");
        }
    }

    private void ObserveFloorTerminalTelemetry(FloorRuntime runtime, string reason)
    {
        if (_runTelemetryObserver == null || runtime.RunTelemetry == null)
            return;

        RunFloorTerminalOutcome outcome = reason switch
        {
            "transitioning" when runtime.RunTelemetry.PassageCommitObserved =>
                RunFloorTerminalOutcome.PassageCompleted,
            "player-death" => RunFloorTerminalOutcome.PlayerDeath,
            _ => RunFloorTerminalOutcome.Aborted
        };
        RunFloorTerminalTelemetry terminal = runtime.RunTelemetry.Finish(
            DateTime.UtcNow,
            runtime.Executor?.HasOpenedHoardThisFloor == true,
            outcome,
            reason);
        try
        {
            _runTelemetryObserver.ObserveFloorTerminal(terminal);
        }
        catch (Exception ex)
        {
            Service.Log.Error($"[RunTelemetry] Host floor terminal observer failed: {ex}");
        }
    }
}
