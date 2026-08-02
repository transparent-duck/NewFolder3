using global::Dalamud.Interface.Textures;
using global::Dalamud.Plugin.Services;
using System;
using System.Collections.Generic;
using DeepDungeon.Fsd.Core;
using DeepDungeon.Fsd.Runtime;
using DeepDungeon.Fsd.Dalamud.Debug.BgCollision;
using DeepDungeon.Fsd.Dalamud.GameState;
using DeepDungeon.Fsd.Dalamud.Runtime.Helpers;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using FFXIVClientStructs.FFXIV.Client.Game.InstanceContent;
using DeepDungeon.Fsd.Dalamud.Map;
using DeepDungeon.Fsd.Dalamud.Runtime;
using DeepDungeon.Fsd.Dalamud.Runtime.Entry;

namespace DeepDungeon.Fsd.Dalamud
{
    internal partial class FsdEngine : IDisposable
    {
        private readonly FsdSettings _configuration;
        private readonly FsdExecutionLease _executionLease;
        private readonly string _hostIdentity;
        private readonly string _hostVersion;
        private readonly DetailedMapHostOptions _detailedMapHostOptions;
        private readonly DetailedMapCatalogManager _detailedMapCatalogManager;
        private readonly IFloorEvidenceObserver? _floorEvidenceObserver;
        private readonly IRunTelemetryObserver? _runTelemetryObserver;
        private readonly FsdStartAuthorizationCallback? _tryAuthorizeFsdStart;
        private readonly NativeDeepDungeonLogMessageSource _logMessageSource;
        
		private RunHost? _ddHost = null;
        private int _fsfScenarioIndex = 1;
        private bool _fsfLoopInfinite = false;
        private int _fsfLoopCount = 1;
        
        private bool _currentInDeepDungeon = false;
        private DeepDungeonStateSnapshot _currentDeepDungeonState = new(
            IsValid: true,
            IsInDeepDungeonTerritory: false,
            IsInDuty: false,
            DungeonId: 0,
            Floor: 0,
            FloorKind: DeepDungeonFloorKind.Unknown,
            IsTransitioning: false,
            Revision: 0);
        private bool _ddRuntimeInit = false;
        private readonly BgCollisionDebugOverlay _bgCollisionDebug = new();
        private bool _showBgCollisionOverlay;
        private readonly Dictionary<uint, PomanderVisual[]> _pomanderVisualCache = new();
		private readonly Dictionary<uint, MagiciteVisual[]> _magiciteVisualCache = new();
        private readonly Dictionary<uint, ISharedImmediateTexture?> _iconTextureCache = new();

        // Shared DD observation and recovery run whenever in deep dungeon, independent of FSD.
        private DutyState? _dutyState;
        private RecoveryPotionHelper? _recoveryPotion;
        private RunContext? _bridgeDeleteSaveContext;
        private GenericDeleteSaveFlow? _bridgeDeleteSaveFlow;
        private DateTime _bridgeDeleteSaveTimeoutAt = DateTime.MinValue;
        private int _bridgeDeleteSaveSlotIndex = -1;
        private string _bridgeDeleteSaveStatus = string.Empty;
        private bool _bridgeDeleteSaveStatusIsError;
        private static readonly TimeSpan BridgeDeleteSaveTimeout = TimeSpan.FromSeconds(45);
        private RunContext? _bridgeLeaveDutyContext;
        private LeaveDutyFlow? _bridgeLeaveDutyFlow;
        private PilgrimsTraverseRestExitFlow? _bridgeLeaveDutyRestExitFlow;
        private DateTime _bridgeLeaveDutyTimeoutAt = DateTime.MinValue;
        private string _bridgeLeaveDutyStatus = string.Empty;
        private bool _bridgeLeaveDutyStatusIsError;
        private static readonly TimeSpan BridgeLeaveDutyTimeout = TimeSpan.FromSeconds(75);

        // Passage follow-up when only room center is available
        private bool _pendingPassageFollowup;
        private uint _pendingPassageDungeonId;
        private byte _pendingPassageFloor;
        private int _pendingPassageRoomIndex = -1;
        private DateTime _pendingPassageRetryAt = DateTime.MinValue;
        private DateTime _pendingPassageTimeout = DateTime.MinValue;

        internal FsdEngine(
            FsdSettings configuration,
            FsdExecutionLease executionLease,
            string hostIdentity,
            string hostVersion,
            string pluginConfigDirectory,
            DetailedMapHostOptions detailedMapHostOptions,
            IFloorEvidenceObserver? floorEvidenceObserver,
            IRunTelemetryObserver? runTelemetryObserver,
            FsdStartAuthorizationCallback? tryAuthorizeFsdStart = null)
        {
            _configuration = configuration;
            _executionLease = executionLease;
            _hostIdentity = hostIdentity;
            _hostVersion = hostVersion;
            _detailedMapHostOptions = detailedMapHostOptions;
            _detailedMapCatalogManager = new DetailedMapCatalogManager(
                pluginConfigDirectory,
                detailedMapHostOptions);
            _floorEvidenceObserver = floorEvidenceObserver;
            _runTelemetryObserver = runTelemetryObserver;
            _tryAuthorizeFsdStart = tryAuthorizeFsdStart;
            _logMessageSource = new NativeDeepDungeonLogMessageSource(Service.GameInteropProvider);
        }

        internal DutyState? DutyState => _dutyState;
        internal bool IsRunActive => _ddHost?.FsdActive ?? false;
        internal string? ActiveDetailedMapReleaseId =>
            _detailedMapCatalogManager.ActiveRunReleaseId;
        public DeepDungeonStateSnapshot CurrentDeepDungeonState => _currentDeepDungeonState;

        private void EnsureGeneralAssists()
        {
            if (_dutyState != null) return;
            _dutyState = new DutyState();
            _recoveryPotion = new RecoveryPotionHelper(_configuration);
        }

        public void Initialize()
        {
            EnsureGeneralAssists();
            // Subscribe to territory changes
            Service.ClientState.TerritoryChanged += OnTerritoryChanged;
            // Restore persisted scenario selection
            try
            {
                _fsfScenarioIndex = GetEffectiveScenarioIndex();
                _fsfLoopInfinite = _configuration.NecromancerFsdLoopInfinite;
                _fsfLoopCount = Math.Max(1, _configuration.NecromancerFsdLoopCount);
            }
            catch
            {
                _fsfScenarioIndex = 1;
                _fsfLoopInfinite = false;
                _fsfLoopCount = 1;
            }
            
            // Check initial territory state
            CheckDeepDungeonState();
            RefreshDeepDungeonStateSnapshot();
        }

        private int GetEffectiveScenarioIndex()
        {
            int configuredIndex = _configuration.NecromancerFsdScenarioIndex == 3
                ? 1
                : _configuration.NecromancerFsdScenarioIndex;
            int maximumIndex = _detailedMapHostOptions.SupportsControlledPtSurvey ? 2 : 1;
            return Math.Clamp(configuredIndex, 0, maximumIndex);
        }

        private void OnTerritoryChanged(uint territoryId)
        {
            try
            {
                CheckDeepDungeonState();
            }
            catch (Exception ex)
            {
                Service.Log.Error($"[FsdEngine] Error in OnTerritoryChanged: {ex}");
            }
        }

        private void CheckDeepDungeonState()
        {
            try
            {
                bool wasInDeepDungeon = _currentInDeepDungeon;
                _currentInDeepDungeon = DeepDungeonHelper.IsInDeepDungeon();
                
                if (_currentInDeepDungeon && !wasInDeepDungeon)
                {
                    // Entering deep dungeon - create new state
                    EnterDeepDungeon();
                }
                else if (!_currentInDeepDungeon && wasInDeepDungeon)
                {
                    // Exiting deep dungeon - dispose state
                    ExitDeepDungeon();
                }

                RefreshDeepDungeonStateSnapshot();
            }
            catch (Exception ex)
            {
                Service.Log.Error($"[FsdEngine] Error checking deep dungeon state: {ex}");
            }
        }
        
        private void EnterDeepDungeon()
        {
            try
            {
                // General assists: create if not yet alive
                if (_dutyState == null)
                {
                    EnsureGeneralAssists();
                }

				if (_ddHost == null)
				{
					var dutyState = _dutyState
						?? throw new InvalidOperationException("Shared deep-dungeon state was not initialized.");
					_ddHost = new RunHost(
						_configuration,
						dutyState,
						_logMessageSource,
						_floorEvidenceObserver,
						_runTelemetryObserver);
					Service.Log.Info("[FsdEngine] Run host created.");
				}

                TryInvokePalacePalFetch();
            }
            catch (Exception ex)
            {
                Service.Log.Error($"[FsdEngine] Error entering deep dungeon: {ex}");
            }
        }
        
        private void ExitDeepDungeon()
        {
            try
            {
                ClearPendingPassageFollowup();
                ResetRoomPresentation();
                if (_ddRuntimeInit)
                {
                    MapPosGeneration.OnExitDeepDungeon();
                    _ddRuntimeInit = false;
                }

			bool fsdRunning = _ddHost?.FsdActive == true;
			if (!fsdRunning)
				{
				try { _ddHost?.Dispose(); } catch { }
				_ddHost = null;
					Service.Log.Info("[FsdEngine] Exited deep dungeon - host disposed");
				}
				else
				{
					Service.Log.Info("[FsdEngine] Exited deep dungeon (host kept alive for FSD multi-loop)");
				}

				_dutyState?.MarkOutsideDuty();
				RefreshDeepDungeonStateSnapshot();
				
            }
            catch (Exception ex)
            {
                Service.Log.Error($"[FsdEngine] Error exiting deep dungeon: {ex}");
            }
        }

        public void Update(IFramework framework)
        {
            _fsfScenarioIndex = GetEffectiveScenarioIndex();
            string? selectedDetailedMapScenario =
                DetailedMapCatalogManager.GetScenarioKey(
                    _fsfScenarioIndex);
            _detailedMapCatalogManager.Update(
                _configuration.UseDetailedMap &&
                _detailedMapHostOptions.HasOnlineCatalogService,
                selectedDetailedMapScenario,
                _ddHost?.FsdActive == true);

            bool dutyStateOk = true;

            // General assists: tick whenever in deep dungeon, independent of FSD.
            if (_currentInDeepDungeon && _dutyState != null)
            {
                dutyStateOk = _dutyState.Update(framework);
                if (dutyStateOk)
                {
                    try { _recoveryPotion?.Update(); } catch { }
                }
            }
            RefreshDeepDungeonStateSnapshot();

            // Tick the host to allow entry flows outside duty (FSD mode)
            _ddHost?.Update(framework);
            if (_detailedMapCatalogManager.ActiveRunSnapshot != null &&
                _ddHost?.FsdActive != true)
            {
                _detailedMapCatalogManager.ReleaseRunSnapshot();
            }
            if (_executionLease.IsHeld && _ddHost?.FsdActive != true)
                _executionLease.Release();
            UpdateBridgeLeaveDuty(framework);
            UpdateBridgeDeleteSaveSlot(framework);

            if (!dutyStateOk) return;

            if (!_currentInDeepDungeon) return;

            try
            {

                // Runtime map center sampling & lifecycle (load/save JSON)
                unsafe
                {
                    var efw = EventFramework.Instance();
                    var dd = efw != null ? efw->GetInstanceContentDeepDungeon() : null;
                    if (dd != null)
                    {
                        if (!_ddRuntimeInit)
                        {
                            DeepDungeon.Fsd.Dalamud.Map.MapPosGeneration.OnEnterDeepDungeon(dd);
                            EnsureDeepDungeonLandmarkSprites();
                            _ddRuntimeInit = true;
                        }

                        if (_dutyState is
                            {
                                IsInDuty: true,
                                IsTransitioning: false,
                                CurrentFloorKind: DeepDungeonFloorKind.Mob
                            } &&
                            _dutyState.IsPlayerPositionStable())
                        {
                            DeepDungeon.Fsd.Dalamud.Map.MapPosGeneration.EnsureCentersAvailable(dd);
                        }
                    }
                    else
                    {
                        if (_ddRuntimeInit)
                        {
                            DeepDungeon.Fsd.Dalamud.Map.MapPosGeneration.OnExitDeepDungeon();
                            _ddRuntimeInit = false;
                        }
                    }
                }

			ProcessPendingPassageFollowup();

            }
            catch (Exception ex)
            {
                Service.Log.Error($"[FsdEngine] Error in Update: {ex}");
            }
        }

        private void RefreshDeepDungeonStateSnapshot()
        {
            var duty = _dutyState;
            var candidate = !_currentInDeepDungeon
                ? new DeepDungeonStateSnapshot(
                    IsValid: true,
                    IsInDeepDungeonTerritory: false,
                    IsInDuty: false,
                    DungeonId: 0,
                    Floor: 0,
                    FloorKind: DeepDungeonFloorKind.Unknown,
                    IsTransitioning: false,
                    Revision: _currentDeepDungeonState.Revision)
                : new DeepDungeonStateSnapshot(
                    IsValid: duty != null && !duty.StateReadFailed,
                    IsInDeepDungeonTerritory: true,
                    IsInDuty: duty?.IsInDuty == true,
                    DungeonId: duty?.DungeonId ?? 0,
                    Floor: duty?.Floor ?? 0,
                    FloorKind: duty?.CurrentFloorKind ?? DeepDungeonFloorKind.Unknown,
                    IsTransitioning: duty?.IsTransitioning == true || duty?.IsInDuty != true,
                    Revision: _currentDeepDungeonState.Revision);

            if (_currentDeepDungeonState.SemanticallyEquals(candidate))
                return;

            _currentDeepDungeonState = candidate with
            {
                Revision = checked(_currentDeepDungeonState.Revision + 1)
            };
        }

        private void UpdateBridgeDeleteSaveSlot(IFramework framework)
        {
            if (_bridgeDeleteSaveFlow == null || _bridgeDeleteSaveContext == null)
                return;

            if (DateTime.Now > _bridgeDeleteSaveTimeoutAt)
            {
                _bridgeDeleteSaveStatus = "PT save delete timed out; move to the PT entry NPC and retry if needed.";
                _bridgeDeleteSaveStatusIsError = true;
                StopBridgeDeleteSaveSlotSession();
                return;
            }

            try
            {
                if (_bridgeDeleteSaveFlow.Update(framework))
                {
                    _bridgeDeleteSaveStatus = _bridgeDeleteSaveContext.StatusLine;
                    _bridgeDeleteSaveStatusIsError = _bridgeDeleteSaveContext.StatusIsError;
                    StopBridgeDeleteSaveSlotSession();
                    return;
                }

                _bridgeDeleteSaveStatus = _bridgeDeleteSaveContext.StatusLine;
                _bridgeDeleteSaveStatusIsError = _bridgeDeleteSaveContext.StatusIsError;
            }
            catch (Exception ex)
            {
                _bridgeDeleteSaveStatus = $"PT save delete error: {ex.Message}";
                _bridgeDeleteSaveStatusIsError = true;
                Service.Log.Error($"[FsdEngine] PT save delete bridge session error: {ex}");
                StopBridgeDeleteSaveSlotSession();
            }
        }

        private void StopBridgeDeleteSaveSlotSession()
        {
            try { _bridgeDeleteSaveContext?.Dispose(); } catch { }
            try { GameState.DeepDungeonUi.CloseDeepDungeonEntryWindows(); } catch { }
            _bridgeDeleteSaveContext = null;
            _bridgeDeleteSaveFlow = null;
            _bridgeDeleteSaveTimeoutAt = DateTime.MinValue;
            _bridgeDeleteSaveSlotIndex = -1;
        }

        private void UpdateBridgeLeaveDuty(IFramework framework)
        {
            if (_bridgeLeaveDutyContext == null)
                return;

            if (DateTime.Now > _bridgeLeaveDutyTimeoutAt)
            {
                _bridgeLeaveDutyStatus = "Leave duty timed out; inspect current duty/rest-room UI before retrying.";
                _bridgeLeaveDutyStatusIsError = true;
                StopBridgeLeaveDutySession();
                return;
            }

            try
            {
                if (_bridgeLeaveDutyContext.Duty.IsInDuty)
                {
                    if (_bridgeLeaveDutyFlow == null)
                    {
                        _bridgeLeaveDutyStatus = "Leave duty flow is unavailable.";
                        _bridgeLeaveDutyStatusIsError = true;
                        StopBridgeLeaveDutySession();
                        return;
                    }

                    if (!_bridgeLeaveDutyFlow.Update(framework))
                    {
                        _bridgeLeaveDutyStatus = _bridgeLeaveDutyContext.StatusLine;
                        _bridgeLeaveDutyStatusIsError = _bridgeLeaveDutyContext.StatusIsError;
                        return;
                    }

                    _bridgeLeaveDutyStatus = "Leave: duty exited; handling PT rest exit.";
                    _bridgeLeaveDutyStatusIsError = false;
                }

                _bridgeLeaveDutyRestExitFlow ??= new PilgrimsTraverseRestExitFlow();
                if (!_bridgeLeaveDutyRestExitFlow.IsPrepared)
                {
                    _bridgeLeaveDutyRestExitFlow.Prepare(_bridgeLeaveDutyContext);
                }

                if (!_bridgeLeaveDutyRestExitFlow.Update(framework))
                {
                    _bridgeLeaveDutyStatus = _bridgeLeaveDutyContext.StatusLine;
                    _bridgeLeaveDutyStatusIsError = _bridgeLeaveDutyContext.StatusIsError;
                    return;
                }

                if (_currentInDeepDungeon)
                {
                    _bridgeLeaveDutyStatus = "Leave: PT rest exit flow complete; waiting for territory exit.";
                    _bridgeLeaveDutyStatusIsError = false;
                    return;
                }

                _bridgeLeaveDutyStatus = "Leave: duty and PT rest exit completed.";
                _bridgeLeaveDutyStatusIsError = false;
                StopBridgeLeaveDutySession();
            }
            catch (Exception ex)
            {
                _bridgeLeaveDutyStatus = $"Leave duty error: {ex.Message}";
                _bridgeLeaveDutyStatusIsError = true;
                Service.Log.Error($"[FsdEngine] Leave duty bridge session error: {ex}");
                StopBridgeLeaveDutySession();
            }
        }

        private void StopBridgeLeaveDutySession()
        {
            try { _bridgeLeaveDutyRestExitFlow?.Dispose(); } catch { }
            try { _bridgeLeaveDutyContext?.Dispose(); } catch { }
            _bridgeLeaveDutyContext = null;
            _bridgeLeaveDutyFlow = null;
            _bridgeLeaveDutyRestExitFlow = null;
            _bridgeLeaveDutyTimeoutAt = DateTime.MinValue;
        }
        

        public void Dispose()
        {
            Service.ClientState.TerritoryChanged -= OnTerritoryChanged;
            ResetRoomPresentation();
            _detailedMapCatalogManager.ReleaseRunSnapshot();
            _detailedMapCatalogManager.Dispose();

            if (_ddRuntimeInit)
            {
                MapPosGeneration.OnExitDeepDungeon();
                _ddRuntimeInit = false;
            }
            
            try { _ddHost?.Dispose(); } catch { }
            _ddHost = null;
            try { _logMessageSource.Dispose(); } catch { }
            StopBridgeLeaveDutySession();
            if (_bridgeDeleteSaveContext != null || _bridgeDeleteSaveFlow != null)
                StopBridgeDeleteSaveSlotSession();

            _dutyState = null;
            _recoveryPotion = null;
            if (_executionLease.IsHeld)
                _executionLease.Release();
        }
    }
}
