using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using DeepDungeon.Fsd.Core;
using DeepDungeon.Fsd.Dalamud.GameState;
using DeepDungeon.Fsd.Dalamud.Runtime;
using DeepDungeon.Fsd.Dalamud.Runtime.Entry;
using DeepDungeon.Fsd.Dalamud.Runtime.Scenarios;
using global::Dalamud.Game.ClientState.Conditions;
using global::Dalamud.Game.ClientState.Objects.Types;
using FFXIVClientStructs.FFXIV.Client.Game.Event;

namespace DeepDungeon.Fsd.Dalamud
{
	internal partial class FsdEngine
	{
		private const string PilgrimsTraverseFsdConfirmation = "start-pt-fsd";
		private const string PilgrimsTraverseDeleteSaveConfirmation = "delete-pt-save-slot";
		private const string DeepDungeonLeaveDutyConfirmation = "leave-deep-dungeon";
		private const uint PilgrimsTraverseNpcDataId = 1054942;
		private const float PilgrimsTraverseEntryAutoMoveMaxDistance = 30f;

		public object StartPilgrimsTraverseFsd(int startFloor, int targetLoops, bool infinite, string? confirmation, string? leaveModeOverride = null)
		{
			if (!string.Equals(confirmation, PilgrimsTraverseFsdConfirmation, StringComparison.Ordinal))
			{
				return new
				{
					ok = false,
					error = $"Starting real Deep Dungeon FSD requires confirm={PilgrimsTraverseFsdConfirmation}.",
					snapshot = GetMobPilotSnapshot()
				};
			}

			if (startFloor is not 21 and not 31)
			{
				return new
				{
					ok = false,
					error = "Only Pilgrim's Traverse 21 or 31 starts are supported."
				};
			}

			if (!TryAuthorizeOutsideDutyOperation(OutsideDutyOperation.StartOrEnter, out var operationError))
			{
				return new
				{
					ok = false,
					error = operationError,
					snapshot = GetMobPilotSnapshot()
				};
			}

			if (_currentInDeepDungeon)
			{
				return new
				{
					ok = false,
					error = "Player is already in a Deep Dungeon territory.",
					snapshot = GetMobPilotSnapshot()
				};
			}

			if (TryGetDeepDungeonEntryUiNames(out var openUiNames))
			{
				return new
				{
					ok = false,
					error = "Deep Dungeon or dialog UI is open; close it before starting FSD.",
					openUi = openUiNames,
					snapshot = GetMobPilotSnapshot()
				};
			}

			var player = Service.LocalPlayer;
			var npc = FindObjectByBaseId(PilgrimsTraverseNpcDataId);
			if (player == null)
			{
				return new
				{
					ok = false,
					error = "Local player is unavailable.",
					snapshot = GetMobPilotSnapshot()
				};
			}

			if (player.IsDead)
			{
				return new
				{
					ok = false,
					error = "Local player is dead.",
					snapshot = GetMobPilotSnapshot()
				};
			}

			if (Service.Condition[ConditionFlag.InCombat])
			{
				return new
				{
					ok = false,
					error = "Local player is in combat.",
					snapshot = GetMobPilotSnapshot()
				};
			}

			if (DeepDungeon.Fsd.Dalamud.moveHelper.VNav.Path.IsRunning() || DeepDungeon.Fsd.Dalamud.moveHelper.VNav.Path.NumWaypoints() > 0)
			{
				return new
				{
					ok = false,
					error = "VNav pathing is still active.",
					snapshot = GetMobPilotSnapshot()
				};
			}

			if (npc == null)
			{
				return new
				{
					ok = false,
					error = "Pilgrim's Traverse entry NPC is not loaded.",
					snapshot = GetMobPilotSnapshot()
				};
			}

			var npcDistance = Vector3.Distance(player.Position, npc.Position);
			if (npcDistance > PilgrimsTraverseEntryAutoMoveMaxDistance)
			{
				return new
				{
					ok = false,
					error = $"Pilgrim's Traverse entry NPC is too far for automatic entry movement ({npcDistance:F1}m).",
					snapshot = GetMobPilotSnapshot()
				};
			}

			int requestedLoops = Math.Max(1, targetLoops);
			if (!TryStartOutsideDutyFsd(
				    () => new PTChestScenario(startFloor),
				    requestedLoops,
				    infinite,
				    startFloor == 21
					    ? DetailedMapScenarioCatalog.PilgrimsTraverse21To30.Key
					    : DetailedMapScenarioCatalog.PilgrimsTraverse31To40.Key,
				    out var startError))
			{
				return new
				{
					ok = false,
					error = startError,
					snapshot = GetMobPilotSnapshot()
				};
			}

			_bridgeDeleteSaveStatus = string.Empty;
			_bridgeDeleteSaveStatusIsError = false;
			_fsfScenarioIndex = startFloor == 21 ? 0 : 1;
			_configuration.NecromancerFsdScenarioIndex = _fsfScenarioIndex;
			_configuration.Save();
			var startedHost = _ddHost!;
			if (!ApplyLeaveModeOverride(startedHost, leaveModeOverride, out var leaveModeError, out var appliedLeaveMode))
			{
				startedHost.StopFsd();
				return new
				{
					ok = false,
					error = leaveModeError,
					snapshot = GetMobPilotSnapshot()
				};
			}
			return new
			{
				ok = true,
				hostIdentity = _hostIdentity,
				hostVersion = _hostVersion,
				fsdEngineVersion = FsdEngineIdentity.InformationalVersion,
				startFloor,
				targetLoops = requestedLoops,
				infinite,
				leaveMode = appliedLeaveMode,
				snapshot = GetMobPilotSnapshot()
			};
		}

		public object StopDeepDungeonFsd()
		{
			if (_ddHost == null || !_ddHost.FsdActive)
			{
				return new
				{
					ok = false,
					error = "Deep Dungeon FSD is not active.",
					snapshot = GetMobPilotSnapshot()
				};
			}

			_ddHost.StopFsd();
			if (_ddHost.FsdActive)
			{
				return new
				{
					ok = false,
					error = "Deep Dungeon FSD stop request did not stop the active session.",
					snapshot = GetMobPilotSnapshot()
				};
			}

			return new
			{
				ok = true,
				snapshot = GetMobPilotSnapshot()
			};
		}

		public object ArmControlledReusableSaveSurveyCapture()
		{
			if (!_detailedMapHostOptions.SupportsControlledPtSurvey)
			{
				return new
				{
					ok = false,
					error = "Controlled reusable-save survey capture is unavailable for this FSD host."
				};
			}

			if (_ddHost?.FsdActive != true)
			{
				return new
				{
					ok = false,
					error = "Controlled reusable-save survey capture requires an active FSD session."
				};
			}

			return _ddHost.ArmControlledReusableSaveSurveyCapture();
		}

		public object GetPilgrimsTraverseFsdPreflight(int startFloor)
		{
			var blockers = new List<string>();
			var warnings = new List<string>();
			var player = Service.LocalPlayer;
			var npc = FindObjectByBaseId(PilgrimsTraverseNpcDataId);
			var saveDataOpen = DeepDungeonUi.IsAddonOpen("DeepDungeonSaveData");
			var menuOpen = DeepDungeonUi.IsAddonOpen("DeepDungeonMenu");
			var selectYesnoOpen = DeepDungeonUi.IsAddonOpen("SelectYesno");
			var selectStringOpen = DeepDungeonUi.IsAddonOpen("SelectString");
			var talkOpen = DeepDungeonUi.IsAddonOpen("Talk") || DeepDungeonUi.IsAddonOpen("EventTalk");
			var contentsFinderConfirmOpen = DeepDungeonUi.IsAddonOpen("ContentsFinderConfirm");
			var saveSlots = BuildBridgeSaveSlotSnapshot(out var saveSlotsObservable, out var emptySaveSlotCount);
			var vnavRunning = DeepDungeon.Fsd.Dalamud.moveHelper.VNav.Path.IsRunning();
			var vnavWaypoints = DeepDungeon.Fsd.Dalamud.moveHelper.VNav.Path.NumWaypoints();
			var inCombat = Service.Condition[ConditionFlag.InCombat];
			var dutyStateAvailable = _dutyState != null;
			float? npcDistance = null;

			if (startFloor is not 21 and not 31)
				blockers.Add("Only Pilgrim's Traverse 21 or 31 starts are supported.");

			if (!dutyStateAvailable)
				warnings.Add("Deep Dungeon duty state is not initialized yet; start-pt will initialize it before starting.");

			if (_currentInDeepDungeon)
				blockers.Add("Player is already in a Deep Dungeon territory.");

			var operationDecision = GetOutsideDutyOperationDecision(OutsideDutyOperation.StartOrEnter);
			if (!operationDecision.Allowed)
				blockers.Add(BuildOutsideDutyOperationConflictError(OutsideDutyOperation.StartOrEnter, operationDecision.Conflict));

			if (player == null)
			{
				blockers.Add("Local player is unavailable.");
			}
			else
			{
				if (player.IsDead)
					blockers.Add("Local player is dead.");
				if (inCombat)
					blockers.Add("Local player is in combat.");
			}

			if (vnavRunning || vnavWaypoints > 0)
				blockers.Add("VNav pathing is still active.");

			if (saveDataOpen || menuOpen || selectYesnoOpen || selectStringOpen || talkOpen || contentsFinderConfirmOpen)
				blockers.Add("Deep Dungeon or dialog UI is open; close it before starting FSD.");

			if (saveDataOpen && !saveSlotsObservable)
				blockers.Add("DeepDungeonSaveData is open but save slot state cannot be read.");

			if (saveDataOpen && emptySaveSlotCount == 0)
				blockers.Add("No empty Pilgrim's Traverse save slot is available.");

			if (npc == null)
			{
				blockers.Add("Pilgrim's Traverse entry NPC is not loaded.");
			}
			else if (player != null)
			{
				npcDistance = Vector3.Distance(player.Position, npc.Position);
				if (npcDistance.Value > PilgrimsTraverseEntryAutoMoveMaxDistance)
					blockers.Add($"Pilgrim's Traverse entry NPC is too far for automatic entry movement ({npcDistance.Value:F1}m).");
				else if (npcDistance.Value > NpcInteractionGuard.MaxInteractDistance)
					warnings.Add($"Pilgrim's Traverse entry NPC is outside direct interaction range ({npcDistance.Value:F1}m); entry flow will move to it.");
			}

			if (player != null && npc == null && !_currentInDeepDungeon)
				warnings.Add("If the player is not at the Pilgrim's Traverse entry area, transport near Vanthau before starting FSD.");

			var snapshot = GetMobPilotSnapshot();
			return new
			{
				ok = true,
				ready = blockers.Count == 0,
				startFloor,
				blockers = blockers.ToArray(),
				warnings = warnings.ToArray(),
				player = player == null
					? null
					: new
					{
						name = player.Name.ToString(),
						player.IsDead,
						inCombat,
						position = SnapshotVector(player.Position)
					},
				ptNpc = new
				{
					baseId = PilgrimsTraverseNpcDataId,
					found = npc != null,
					distance = npcDistance,
					maxInteractDistance = NpcInteractionGuard.MaxInteractDistance,
					autoMoveMaxDistance = PilgrimsTraverseEntryAutoMoveMaxDistance,
					position = SnapshotVector(npc?.Position)
				},
				ui = new
				{
					deepDungeonSaveData = saveDataOpen,
					deepDungeonMenu = menuOpen,
					selectYesno = selectYesnoOpen,
					selectString = selectStringOpen,
					talk = talkOpen,
					contentsFinderConfirm = contentsFinderConfirmOpen
				},
				saveSlots,
				vnav = new
				{
					running = vnavRunning,
					waypoints = vnavWaypoints
				},
				dutyState = new
				{
					available = dutyStateAvailable,
					willInitializeOnStart = !dutyStateAvailable
				},
				snapshot
			};
		}

		public object CloseDeepDungeonEntryWindowsForBridge()
		{
			if (_ddHost?.AssistModeActive == true)
			{
				return new
				{
					ok = false,
					error = "Refusing to close Deep Dungeon UI while automation is active.",
					snapshot = GetMobPilotSnapshot()
				};
			}

			if (_bridgeLeaveDutyContext != null)
			{
				return new
				{
					ok = false,
					error = "Refusing to close Deep Dungeon UI while a leave-duty session is active.",
					snapshot = GetMobPilotSnapshot()
				};
			}

			if (_bridgeDeleteSaveContext != null || _bridgeDeleteSaveFlow != null)
			{
				return new
				{
					ok = false,
					error = "Refusing to close Deep Dungeon UI while a save-delete session is active.",
					snapshot = GetMobPilotSnapshot()
				};
			}

			DeepDungeonUi.CloseDeepDungeonEntryWindows();
			_bridgeDeleteSaveStatus = string.Empty;
			_bridgeDeleteSaveStatusIsError = false;
			return new
			{
				ok = true,
				snapshot = GetMobPilotSnapshot()
			};
		}

		public object StartPilgrimsTraverseDeleteSaveSlot(int slotNumber, string? confirmation)
		{
			if (!string.Equals(confirmation, PilgrimsTraverseDeleteSaveConfirmation, StringComparison.Ordinal))
			{
				return new
				{
					ok = false,
					error = $"Deleting a real Pilgrim's Traverse save requires confirm={PilgrimsTraverseDeleteSaveConfirmation}.",
					snapshot = GetMobPilotSnapshot()
				};
			}

			if (slotNumber is not 1 and not 2)
			{
				return new
				{
					ok = false,
					error = "slotNumber must be 1 or 2.",
					snapshot = GetMobPilotSnapshot()
				};
			}

			if (!TryAuthorizeOutsideDutyOperation(OutsideDutyOperation.DeleteSave, out var operationError))
			{
				return new
				{
					ok = false,
					error = operationError,
					snapshot = GetMobPilotSnapshot()
				};
			}

			if (_currentInDeepDungeon)
			{
				return new
				{
					ok = false,
					error = "Refusing to delete a PT save while inside Deep Dungeon.",
					snapshot = GetMobPilotSnapshot()
				};
			}

			if (TryGetDeepDungeonEntryUiNames(out var openUiNames))
			{
				return new
				{
					ok = false,
					error = "Deep Dungeon or dialog UI is open; close it before deleting a save.",
					openUi = openUiNames,
					snapshot = GetMobPilotSnapshot()
				};
			}

			EnsureGeneralAssists();
			if (_dutyState == null)
			{
				return new
				{
					ok = false,
					error = "Deep Dungeon duty state is unavailable.",
					snapshot = GetMobPilotSnapshot()
				};
			}

			var slotIndex = slotNumber - 1;
			_bridgeDeleteSaveContext = new RunContext(
				_configuration,
				_dutyState,
				new DetailedMapRunSnapshot(
					DetailedMapRuntimePolicy.PalacePalOnly,
					scenarioKey: null,
					catalog: null));
			_bridgeDeleteSaveFlow = new GenericDeleteSaveFlow(DungeonCatalog.PilgrimsTraverse, slotIndex);
			_bridgeDeleteSaveFlow.Prepare(_bridgeDeleteSaveContext);
			_bridgeDeleteSaveTimeoutAt = DateTime.Now.Add(BridgeDeleteSaveTimeout);
			_bridgeDeleteSaveSlotIndex = slotIndex;
			_bridgeDeleteSaveStatus = $"PT save delete started for slot {slotNumber}.";
			_bridgeDeleteSaveStatusIsError = false;
			Service.Log.Warning($"[FsdEngine] Explicit PT save delete bridge session started for slot {slotNumber}.");

			return new
			{
				ok = true,
				slotNumber,
				snapshot = GetMobPilotSnapshot()
			};
		}

		public object StartDeepDungeonLeaveDuty(string? confirmation)
		{
			if (!string.Equals(confirmation, DeepDungeonLeaveDutyConfirmation, StringComparison.Ordinal))
			{
				return new
				{
					ok = false,
					error = $"Leaving a real Deep Dungeon duty requires confirm={DeepDungeonLeaveDutyConfirmation}.",
					snapshot = GetMobPilotSnapshot()
				};
			}

			if (!TryAuthorizeOutsideDutyOperation(OutsideDutyOperation.LeaveDuty, out var operationError))
			{
				return new
				{
					ok = false,
					error = operationError,
					snapshot = GetMobPilotSnapshot()
				};
			}

			if (!_currentInDeepDungeon)
			{
				return new
				{
					ok = false,
					error = "Player is not in a Deep Dungeon territory.",
					snapshot = GetMobPilotSnapshot()
				};
			}

			EnsureGeneralAssists();
			if (_dutyState == null)
			{
				return new
				{
					ok = false,
					error = "Deep Dungeon duty state is unavailable.",
					snapshot = GetMobPilotSnapshot()
				};
			}

			_bridgeLeaveDutyContext = new RunContext(
				_configuration,
				_dutyState,
				new DetailedMapRunSnapshot(
					DetailedMapRuntimePolicy.PalacePalOnly,
					scenarioKey: null,
					catalog: null));
			if (_bridgeLeaveDutyContext.Duty.IsInDuty)
			{
				_bridgeLeaveDutyFlow = new LeaveDutyFlow();
				_bridgeLeaveDutyFlow.Prepare(_bridgeLeaveDutyContext);
			}
			else
			{
				_bridgeLeaveDutyFlow = null;
			}
			_bridgeLeaveDutyRestExitFlow = null;
			_bridgeLeaveDutyTimeoutAt = DateTime.Now.Add(BridgeLeaveDutyTimeout);
			_bridgeLeaveDutyStatus = _bridgeLeaveDutyContext.Duty.IsInDuty
				? "Leave: explicit leave-duty started."
				: "Leave: explicit PT rest exit started.";
			_bridgeLeaveDutyStatusIsError = false;
			Service.Log.Warning("[FsdEngine] Explicit Deep Dungeon leave-duty bridge session started.");

			return new
			{
				ok = true,
				snapshot = GetMobPilotSnapshot()
			};
		}

		public unsafe object GetMobPilotSnapshot()
		{
			var host = _ddHost;
			bool hostActive = host?.AssistModeActive ?? false;
			var activeHost = hostActive ? host : null;
			var status = activeHost?.GetStatusSnapshot();
			var debug = activeHost?.FloorController.GetDebugSnapshot();
			var eventFramework = EventFramework.Instance();
			var dd = eventFramework != null ? eventFramework->GetInstanceContentDeepDungeon() : null;
			var player = Service.LocalPlayer;
			var currentTarget = Service.TargetManager.Target;
			int playerRoom = dd != null ? RoomGraph.GetLocalPlayerRoomIndex(dd) : -1;
			string hostStatus = activeHost?.CurrentStatus ?? string.Empty;
			bool hostStatusIsError = activeHost?.CurrentStatusIsError ?? false;
			string debugStatus = debug?.Status ?? string.Empty;
			string snapshotStatus = hostStatusIsError && !string.IsNullOrWhiteSpace(hostStatus)
				? hostStatus
				: !string.IsNullOrWhiteSpace(debugStatus)
					? debugStatus
					: hostStatus;
			bool leaveDutyActive = _bridgeLeaveDutyContext != null;
			string leaveDutyStatus = leaveDutyActive
				? _bridgeLeaveDutyContext?.StatusLine ?? _bridgeLeaveDutyStatus
				: string.Empty;
			bool leaveDutyStatusIsError = leaveDutyActive && (_bridgeLeaveDutyContext?.StatusIsError ?? _bridgeLeaveDutyStatusIsError);
			if (leaveDutyActive)
			{
				snapshotStatus = string.IsNullOrWhiteSpace(leaveDutyStatus) ? _bridgeLeaveDutyStatus : leaveDutyStatus;
			}
			string mode = host == null || !hostActive
				? leaveDutyActive ? "leave-duty" : "none"
				: host.FsdActive
					? "FSD"
					: "none";
			bool saveDeleteActive = _bridgeDeleteSaveFlow != null;
			var activeRunOptions = activeHost?.RunOptionsProvider?.Current;

			return new
			{
				ok = true,
				hostIdentity = _hostIdentity,
				hostVersion = _hostVersion,
				fsdEngineVersion = FsdEngineIdentity.InformationalVersion,
				inDeepDungeonTerritory = _currentInDeepDungeon,
				mode,
				hostActive,
				scenario = activeHost?.CurrentScenarioName ?? string.Empty,
				completedLoops = activeHost?.CompletedLoops ?? 0,
				targetLoops = activeHost?.TargetLoops ?? 0,
				infiniteLoops = activeHost?.Infinite ?? false,
				leaveMode = activeRunOptions?.LeaveMode.ToString() ?? string.Empty,
				leaveAfterMinutes = activeRunOptions?.LeaveAfterMinutes ?? 0,
				inDuty = status?.inDuty ?? dd != null,
				playerDead = player?.IsDead ?? false,
				inCombat = Service.Condition[ConditionFlag.InCombat],
				vnavRunning = DeepDungeon.Fsd.Dalamud.moveHelper.VNav.Path.IsRunning(),
				vnavWaypoints = DeepDungeon.Fsd.Dalamud.moveHelper.VNav.Path.NumWaypoints(),
				currentTargetId = currentTarget?.GameObjectId ?? 0,
				dungeonId = dd != null ? dd->DeepDungeonId : status?.dungeonId ?? 0,
				floor = dd != null ? dd->Floor : status?.floor ?? 0,
				passageOpen = status?.passageOpen ?? false,
				playerRoom,
				phase = debug?.Phase.ToString() ?? string.Empty,
				taskPhase = debug?.TaskPhase.ToString() ?? string.Empty,
				status = snapshotStatus,
				statusIsError = leaveDutyActive ? leaveDutyStatusIsError : hostStatusIsError,
				hoardCount = debug?.HoardCount ?? 0,
				hoardEvidenceState = debug?.HoardEvidenceState.ToString() ?? string.Empty,
				cachedHoardIndicator = SnapshotVector(debug?.CachedHoardIndicatorPos),
				currentTargetRoom = debug?.RoomPlan.FirstOrDefault()?.RoomIndex,
				roomPath = debug?.RoomPath.ToArray() ?? [],
				completedRooms = debug?.CompletedRooms.OrderBy(room => room).ToArray() ?? [],
				roomPlan = debug?.RoomPlan.Select(entry => new
				{
					entry.RoomIndex,
					entry.ShouldProbeHoard,
					entry.ShouldSearchChests,
					entry.ShouldVisitForIntel,
					hoardEvidenceState = entry.HoardEvidenceState.ToString()
				}).ToArray() ?? [],
				planTrace = debug?.PlanTrace == null
					? null
					: new
					{
						rejectionReason = debug.PlanTrace.RejectionReason ?? string.Empty,
						candidates = debug.PlanTrace.Candidates.Select(candidate => new
						{
							candidate.RoomIndex,
							candidate.Eligible,
							candidate.ShouldProbeHoard,
							candidate.ShouldSearchChests,
							candidate.ShouldVisitForIntel,
							hoardEvidenceState = candidate.HoardEvidenceState.ToString(),
							candidate.BasePriority,
							candidate.Reason
						}).ToArray(),
						selections = debug.PlanTrace.Selections.Select(selection => new
						{
							selection.Step,
							selection.FromRoomIndex,
							selection.SelectedRoomIndex,
							selection.Distance,
							selection.PassageDistance,
							selection.BasePriority
						}).ToArray()
					},
				roomContext = debug?.RoomContext == null
					? null
					: new
					{
						debug.RoomContext.RoomIndex,
						debug.RoomContext.CurrentWaypointIndex,
						waypoints = debug.RoomContext.Waypoints.Select(waypoint => new
						{
							type = waypoint.Type.ToString(),
							position = SnapshotVector(waypoint.Position)
						}).ToArray()
					},
				saveSlots = BuildBridgeSaveSlotSnapshot(out _, out _),
				saveDelete = new
				{
					active = saveDeleteActive,
					slotNumber = saveDeleteActive && _bridgeDeleteSaveSlotIndex >= 0 ? _bridgeDeleteSaveSlotIndex + 1 : (int?)null,
					status = saveDeleteActive ? _bridgeDeleteSaveContext?.StatusLine ?? _bridgeDeleteSaveStatus : string.Empty,
					statusIsError = saveDeleteActive && (_bridgeDeleteSaveContext?.StatusIsError ?? _bridgeDeleteSaveStatusIsError)
				},
				leaveDuty = new
				{
					active = leaveDutyActive,
					status = leaveDutyActive ? snapshotStatus : string.Empty,
					statusIsError = leaveDutyStatusIsError
				},
				recorderPath = activeHost?.FloorController.RunRecorderPath ?? string.Empty,
				lastFsd = new
				{
					status = hostActive ? string.Empty : host?.LastStatus ?? string.Empty,
					statusIsError = !hostActive && (host?.LastStatusIsError ?? false)
				},
				lastSaveDelete = new
				{
					status = saveDeleteActive ? string.Empty : _bridgeDeleteSaveStatus,
					statusIsError = !saveDeleteActive && _bridgeDeleteSaveStatusIsError
				},
				lastLeaveDuty = new
				{
					status = leaveDutyActive ? string.Empty : _bridgeLeaveDutyStatus,
					statusIsError = !leaveDutyActive && _bridgeLeaveDutyStatusIsError
				}
			};
		}

		private static IGameObject? FindObjectByBaseId(uint baseId)
		{
			foreach (var obj in Service.GameObjects)
			{
				if (obj != null && obj.BaseId == baseId)
					return obj;
			}

			return null;
		}

		private static object? SnapshotVector(Vector3? value)
		{
			return value.HasValue ? SnapshotVector(value.Value) : null;
		}

		private static object SnapshotVector(Vector3 value)
		{
			return new
			{
				x = value.X,
				y = value.Y,
				z = value.Z
			};
		}

		private static bool TryGetDeepDungeonEntryUiNames(out string[] openUiNames)
		{
			var names = new List<string>();
			foreach (var name in new[]
			         {
				         "DeepDungeonSaveData",
				         "DeepDungeonMenu",
				         "ContentsFinderConfirm",
				         "SelectYesno",
				         "SelectString",
				         "Talk",
				         "EventTalk",
				         "ContextIconMenu"
			         })
			{
				if (DeepDungeonUi.IsAddonOpen(name))
					names.Add(name);
			}

			openUiNames = names.ToArray();
			return openUiNames.Length > 0;
		}

		private static object BuildBridgeSaveSlotSnapshot(out bool observable, out int? emptyCount)
		{
			observable = false;
			emptyCount = null;
			if (!DeepDungeonUi.IsAddonOpen("DeepDungeonSaveData"))
			{
				return new
				{
					observable = false,
					reason = "DeepDungeonSaveData is not open.",
					slot1Empty = (bool?)null,
					slot2Empty = (bool?)null,
					emptyCount = (int?)null,
					hasEmptySlot = (bool?)null
				};
			}

			if (!DeepDungeonUi.TryGetEmptySlotsFromDeepDungeonSaveData(out var slot1Empty, out var slot2Empty, log: false))
			{
				return new
				{
					observable = false,
					reason = "DeepDungeonSaveData is open but slot state could not be read.",
					slot1Empty = (bool?)null,
					slot2Empty = (bool?)null,
					emptyCount = (int?)null,
					hasEmptySlot = (bool?)null
				};
			}

			observable = true;
			emptyCount = (slot1Empty ? 1 : 0) + (slot2Empty ? 1 : 0);
			return new
			{
				observable = true,
				reason = string.Empty,
				slot1Empty = (bool?)slot1Empty,
				slot2Empty = (bool?)slot2Empty,
				emptyCount,
				hasEmptySlot = emptyCount > 0
			};
		}

		private static bool ApplyLeaveModeOverride(RunHost host, string? leaveModeOverride, out string? error, out string appliedLeaveMode)
		{
			error = null;
			var provider = host.RunOptionsProvider;
			var currentLeaveMode = provider?.Current.LeaveMode ?? LeaveMode.AfterFinishDungeon;
			appliedLeaveMode = $"default:{currentLeaveMode}";
			if (string.IsNullOrWhiteSpace(leaveModeOverride) ||
			    string.Equals(leaveModeOverride, "default", StringComparison.OrdinalIgnoreCase))
			{
				return true;
			}

			LeaveMode requestedMode;
			if (string.Equals(leaveModeOverride, "finish", StringComparison.OrdinalIgnoreCase) ||
			    string.Equals(leaveModeOverride, "afterFinishDungeon", StringComparison.OrdinalIgnoreCase))
			{
				requestedMode = LeaveMode.AfterFinishDungeon;
			}
			else if (string.Equals(leaveModeOverride, "hoard", StringComparison.OrdinalIgnoreCase) ||
			         string.Equals(leaveModeOverride, "afterHoard", StringComparison.OrdinalIgnoreCase))
			{
				requestedMode = LeaveMode.AfterHoard;
			}
			else if (string.Equals(leaveModeOverride, "immediate", StringComparison.OrdinalIgnoreCase))
			{
				requestedMode = LeaveMode.Immediate;
			}
			else if (string.Equals(leaveModeOverride, "boss", StringComparison.OrdinalIgnoreCase) ||
			         string.Equals(leaveModeOverride, "onBossFloorEntry", StringComparison.OrdinalIgnoreCase))
			{
				requestedMode = LeaveMode.OnBossFloorEntry;
			}
			else if (string.Equals(leaveModeOverride, "minutes", StringComparison.OrdinalIgnoreCase) ||
			         string.Equals(leaveModeOverride, "afterNMinutes", StringComparison.OrdinalIgnoreCase))
			{
				requestedMode = LeaveMode.AfterNMinutes;
			}
			else
			{
				error = "Unsupported leaveMode. Use default, finish, hoard, immediate, boss, or minutes.";
				return false;
			}

			if (provider == null)
			{
				error = "Deep Dungeon run options are unavailable.";
				return false;
			}

			provider.Update(options => options.LeaveMode = requestedMode);
			appliedLeaveMode = requestedMode.ToString();
			return true;
		}
	}
}
