using System;
using System.Numerics;
using global::Dalamud.Game.ClientState.Conditions;
using DeepDungeon.Fsd.Dalamud.Runtime.Search;

namespace DeepDungeon.Fsd.Dalamud.Runtime.Floor;

public sealed partial class FloorPhaseController
{
    private void ObserveFloorTelemetryBoundary(FloorExplorationController runtime, string reason)
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

    private void ObserveFloorTerminalTelemetry(FloorExplorationController runtime, string reason)
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
