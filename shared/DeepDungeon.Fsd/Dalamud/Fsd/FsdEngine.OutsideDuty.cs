using System;
using DeepDungeon.Fsd.Core;
using DeepDungeon.Fsd.Dalamud.GameState;
using DeepDungeon.Fsd.Dalamud.Runtime;

namespace DeepDungeon.Fsd.Dalamud
{
    internal partial class FsdEngine
    {
        private const string HostDeniedFsdStartError = "FSD start was denied by the host.";

        private bool TryStartOutsideDutyFsd(
            Func<IScenario> scenarioFactory,
            int targetLoops,
            bool infinite,
            string? detailedMapScenarioKey,
            out string error)
        {
            if (_tryAuthorizeFsdStart is not null && !_tryAuthorizeFsdStart(out _))
            {
                error = HostDeniedFsdStartError;
                Service.Log.Warning($"[FsdEngine] FSD start rejected: {error}");
                return false;
            }

            if (!TryAuthorizeOutsideDutyOperation(OutsideDutyOperation.StartOrEnter, out error))
                return false;

            EnsureGeneralAssists();
            if (_dutyState == null)
            {
                error = "Deep Dungeon duty state is unavailable.";
                Service.Log.Error($"[FsdEngine] FSD start rejected: {error}");
                return false;
            }

            if (!_detailedMapCatalogManager.TryAcquireRunSnapshot(
                    _configuration.UseDetailedMap &&
                    _detailedMapHostOptions.HasOnlineCatalogService,
                    detailedMapScenarioKey,
                    out DetailedMapRunSnapshot detailedMapSnapshot,
                    out error))
            {
                Service.Log.Warning(
                    $"[FsdEngine] FSD start rejected: {error}");
                return false;
            }

            try
            {
                _executionLease.Acquire();
                _ddHost ??= new RunHost(
                    _configuration,
                    _dutyState,
                    _logMessageSource,
                    _floorEvidenceObserver,
                    _runTelemetryObserver);
                _ddHost.StartFsd(
                    scenarioFactory,
                    targetLoops,
                    infinite,
                    detailedMapSnapshot);
            }
            catch (Exception ex)
            {
                _detailedMapCatalogManager.ReleaseRunSnapshot();
                if (_executionLease.IsHeld)
                    _executionLease.Release();
                error = $"FSD execution ownership failed: {ex.Message}";
                Service.Log.Error($"[FSD] Start rejected: {error}");
                return false;
            }
            if (_ddHost.FsdActive)
            {
                error = string.Empty;
                return true;
            }

            error = "Deep Dungeon FSD did not start.";
            _detailedMapCatalogManager.ReleaseRunSnapshot();
            if (_executionLease.IsHeld)
                _executionLease.Release();
            Service.Log.Error($"[FsdEngine] FSD start rejected: {error}");
            return false;
        }

        private bool TryAuthorizeOutsideDutyOperation(OutsideDutyOperation requestedOperation, out string error)
        {
            var decision = GetOutsideDutyOperationDecision(requestedOperation);
            if (decision.Allowed)
            {
                error = string.Empty;
                return true;
            }

            error = BuildOutsideDutyOperationConflictError(requestedOperation, decision.Conflict);
            Service.Log.Warning($"[FsdEngine] Outside-duty operation rejected: {error}");
            return false;
        }

        private OutsideDutyOperationDecision GetOutsideDutyOperationDecision(OutsideDutyOperation requestedOperation)
        {
            return OutsideDutyOperationConflictPlanner.Decide(
                new OutsideDutyOperationSnapshot
                {
                    StartOrEnterActive = _ddHost?.AssistModeActive == true,
                    LeaveDutyActive = _bridgeLeaveDutyContext != null,
                    DeleteSaveActive = _bridgeDeleteSaveContext != null || _bridgeDeleteSaveFlow != null
                },
                requestedOperation);
        }

        private static string BuildOutsideDutyOperationConflictError(
            OutsideDutyOperation requestedOperation,
            OutsideDutyOperationConflict conflict)
        {
            string requested = requestedOperation switch
            {
                OutsideDutyOperation.StartOrEnter => "start or enter Deep Dungeon",
                OutsideDutyOperation.LeaveDuty => "leave Deep Dungeon",
                OutsideDutyOperation.DeleteSave => "delete a Deep Dungeon save",
                _ => "start the requested Deep Dungeon operation"
            };
            string active = conflict switch
            {
                OutsideDutyOperationConflict.StartOrEnterActive => "Deep Dungeon start/enter automation is active",
                OutsideDutyOperationConflict.LeaveDutyActive => "a Deep Dungeon leave-duty session is active",
                OutsideDutyOperationConflict.DeleteSaveActive => "a Deep Dungeon save-delete session is active",
                OutsideDutyOperationConflict.MultipleOperationsActive => "multiple conflicting Deep Dungeon operations are active",
                _ => "the requested Deep Dungeon operation is invalid"
            };
            return $"Cannot {requested}: {active}.";
        }
    }
}
