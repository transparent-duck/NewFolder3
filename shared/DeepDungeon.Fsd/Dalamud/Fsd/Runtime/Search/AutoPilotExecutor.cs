using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using DeepDungeon.Fsd.Core;
using DeepDungeon.Fsd.Dalamud.GameState;
using DeepDungeon.Fsd.Dalamud.Map;
using DeepDungeon.Fsd.Dalamud.Runtime;
using DeepDungeon.Fsd.Dalamud.Runtime.Floor;
using DeepDungeon.Fsd.Dalamud.Runtime.Helpers;
using FFXIVClientStructs.FFXIV.Client.Game.InstanceContent;

namespace DeepDungeon.Fsd.Dalamud.Runtime.Search
{
	/// <summary>
	/// Planning-only executor: keeps floor-scoped search state, rebuilds the room visit plan,
	/// manages per-room waypoint queues, and exposes a query+notification API.
	/// All execution (navigation, timers, distance checks) is handled by FloorPhaseController.
	/// </summary>
	public class AutoPilotExecutor
	{
		private readonly List<RoomPlanEntry> _roomPlan = new();
		private readonly List<RoomPlanEntry> _retainedNonHoardRoute = new();
		private readonly Func<int, (bool HoardSearched, bool ChestsSearched, bool IntelVisited)> _getRoomProgress;
		private readonly PalacePalProvider _palacePalProvider;
		private readonly DetailedMapRunSnapshot _detailedMap;
		private readonly List<Vector3> _observedSightTrapPositions = new();
		private readonly HashSet<int> _blindHoardProbeSuppressedRooms = new();

		private RunOptions _configSnapshot;
		private int _floorHoardBaseline;
		private int _observedHoardCount;
		private bool _hoardOpenedThisFloor;
		private Vector3? _cachedHoardIndicatorPos;
		private RoomSearchContext? _roomContext;
		private byte _lastFloor = 255;
		private HoardEvidenceState _lastHoardEvidenceState = HoardEvidenceState.Disabled;
		private FloorPlanTrace _lastPlanTrace;
		private bool _hasPlanningSnapshot;
		private bool _inheritedNoHoardInferred;

		internal AutoPilotExecutor(
			Func<int, (bool HoardSearched, bool ChestsSearched, bool IntelVisited)> getRoomProgress,
			DetailedMapRunSnapshot detailedMap)
		{
			_getRoomProgress = getRoomProgress;
			_detailedMap = detailedMap ??
				throw new ArgumentNullException(nameof(detailedMap));
			_palacePalProvider = new PalacePalProvider();
			_configSnapshot = new RunOptions();
		}

		// ===== Query properties =====

		public bool IsComplete => _hasPlanningSnapshot && _roomPlan.Count == 0 && _roomContext == null;

		internal bool HasPlanningSnapshot => _hasPlanningSnapshot;

		internal RoomPlanEntry? CurrentPlanEntry => _roomPlan.Count > 0 ? _roomPlan[0] : null;

		public int? CurrentTargetRoomIndex => CurrentPlanEntry?.RoomIndex;

		internal RoomWaypoint? CurrentWaypoint => _roomContext?.CurrentWaypoint;

		internal int RemainingWaypointCount => _roomContext?.RemainingWaypointCount ?? 0;
		internal int PlannedRouteCount => _roomPlan.Count;
		internal IReadOnlyList<RoomPlanEntry> PlannedRoute => _roomPlan;
		internal IReadOnlyList<RoomPlanEntry> RetainedNonHoardRoute => _retainedNonHoardRoute;
		internal FloorPlanTrace LastPlanTrace => _lastPlanTrace;

		public bool HasOpenedHoardThisFloor => _hoardOpenedThisFloor;

		public bool CanAcceptHoardIndicator => HoardIndicatorLifecyclePlanner.Decide(new HoardIndicatorLifecycleSnapshot(
			_hoardOpenedThisFloor,
			_floorHoardBaseline,
			_observedHoardCount)).AcceptIndicator;

		public RunOptions ConfigSnapshot => _configSnapshot;

		internal RoomSearchContext? RoomContext => _roomContext;

		public Vector3? CachedHoardIndicatorPos => _cachedHoardIndicatorPos;

		public IReadOnlyList<Vector3> ObservedSightTrapPositions => _observedSightTrapPositions;

		public bool OpenChestsEnabled => _configSnapshot.OpenBronze || _configSnapshot.OpenSilver || _configSnapshot.OpenGold;

		public HoardEvidenceState HoardEvidenceState => _lastHoardEvidenceState;

		internal unsafe IReadOnlyList<Vector3> GetPalacePalCandidatesForRoom(
			InstanceContentDeepDungeon* dd,
			int roomIndex) =>
			_palacePalProvider.GetCandidatePositionsForRoom(dd, roomIndex);

		public void MarkInheritedNoHoardInferred()
		{
			_inheritedNoHoardInferred = true;
		}

		public bool IsHoardEvidenceUnstable =>
			_lastHoardEvidenceState is HoardEvidenceState.IntuitionPending or HoardEvidenceState.IntuitionWaitingForIndicator;

		public bool IsHoardWorkResolved =>
			_hasPlanningSnapshot &&
			(!_configSnapshot.BandedEnabled ||
			 _hoardOpenedThisFloor ||
			 _lastHoardEvidenceState is HoardEvidenceState.FloorsetMaxed or
				 HoardEvidenceState.FloorsetDistributionExcluded ||
			 _lastHoardEvidenceState == HoardEvidenceState.IntuitionNoHoard ||
			 (_lastHoardEvidenceState == HoardEvidenceState.IntuitionActiveUnconfirmed && !HasPendingIntelWork) ||
			 ((_lastHoardEvidenceState == HoardEvidenceState.BlindSearch ||
			   _lastHoardEvidenceState == HoardEvidenceState.IntuitionDirect) &&
			  !_roomPlan.Any(entry => entry.ShouldProbeHoard)));

		internal bool HasAuthoritativeHoardResolution =>
			_hasPlanningSnapshot &&
			(!_configSnapshot.BandedEnabled ||
			 _hoardOpenedThisFloor ||
			 _lastHoardEvidenceState is HoardEvidenceState.FloorsetMaxed or
				 HoardEvidenceState.FloorsetDistributionExcluded ||
			 _lastHoardEvidenceState == HoardEvidenceState.IntuitionNoHoard);

		internal bool HasPendingBandedWaypoint => CurrentWaypoint?.Type == RoomObjectiveType.ChestBanded;

		internal bool HasPendingIntelWork
		{
			get
			{
				for (int i = 0; i < _roomPlan.Count; i++)
				{
					if (_roomPlan[i].ShouldVisitForIntel)
						return true;
				}

				return false;
			}
		}

		// ===== Lifecycle =====

		public unsafe void ResetForFloor(InstanceContentDeepDungeon* dd, RunOptions configSnapshot)
		{
			_lastFloor = dd->Floor;
			Service.Log.Info($"[AutoPilot] Floor changed to {_lastFloor}, resetting");

			_configSnapshot = configSnapshot.Copy();
			Service.Log.Debug($"[AutoPilot] Floor {_lastFloor} options | banded={_configSnapshot.BandedEnabled} gold={_configSnapshot.OpenGold} silver={_configSnapshot.OpenSilver} bronze={_configSnapshot.OpenBronze} hoardBaseline={dd->HoardCount}");

			_roomPlan.Clear();
			_retainedNonHoardRoute.Clear();
			_roomContext = null;
			_floorHoardBaseline = dd->HoardCount;
			_observedHoardCount = dd->HoardCount;
			_hoardOpenedThisFloor = false;
			_cachedHoardIndicatorPos = null;
			_observedSightTrapPositions.Clear();
			_blindHoardProbeSuppressedRooms.Clear();
			_lastHoardEvidenceState = HoardEvidenceState.Disabled;
			_lastPlanTrace = default;
			_hasPlanningSnapshot = false;
		}

		public bool ApplyRunOptions(RunOptions runOptions)
		{
			if (OptionsEqual(_configSnapshot, runOptions))
				return false;

			_configSnapshot = runOptions.Copy();
			return true;
		}

		public void ObserveHoardCount(int hoardCount)
		{
			_observedHoardCount = hoardCount;
			var decision = HoardIndicatorLifecyclePlanner.Decide(new HoardIndicatorLifecycleSnapshot(
				_hoardOpenedThisFloor,
				_floorHoardBaseline,
				hoardCount));
			if (decision.MarkHoardOpened)
			{
				_hoardOpenedThisFloor = true;
				_cachedHoardIndicatorPos = null;
			}
		}

		public void UpdateCachedHoardIndicator(Vector3 indicatorPosition)
		{
			_cachedHoardIndicatorPos = indicatorPosition;
		}

		public bool ClearCachedHoardIndicator()
		{
			if (!_cachedHoardIndicatorPos.HasValue)
				return false;

			_cachedHoardIndicatorPos = null;
			return true;
		}

		internal void ObserveSightTrapIndicators(FloorObjectEvidenceSnapshot objectEvidence)
		{
			for (int indicatorIndex = 0; indicatorIndex < objectEvidence.SightTrapIndicators.Count; indicatorIndex++)
			{
				Vector3 position = objectEvidence.SightTrapIndicators[indicatorIndex].Position;
				bool duplicate = false;
				for (int existingIndex = 0; existingIndex < _observedSightTrapPositions.Count; existingIndex++)
				{
					if (Vector3.DistanceSquared(_observedSightTrapPositions[existingIndex], position) <=
					    0.100001f * 0.100001f)
					{
						duplicate = true;
						break;
					}
				}
				if (!duplicate)
					_observedSightTrapPositions.Add(position);
			}
		}

		public void ClearRoomContext()
		{
			_roomContext = null;
		}

		internal IReadOnlyList<RoomPlanEntry> SnapshotPlannedRoute()
		{
			return _roomPlan.ToArray();
		}

		internal IReadOnlyList<RoomPlanEntry> SnapshotRetainedNonHoardRoute()
		{
			return _retainedNonHoardRoute.ToArray();
		}

		internal bool ApplyRetainedRoute(IReadOnlyList<RoomPlanEntry> retainedRoute)
		{
			var previousCurrent = CurrentPlanEntry;
			bool currentRemoved = previousCurrent.HasValue &&
			                      (retainedRoute.Count == 0 || retainedRoute[0] != previousCurrent.Value);
			_roomPlan.Clear();
			_roomPlan.AddRange(retainedRoute);
			return currentRemoved;
		}

		public bool IsRoomSearched(int roomIndex)
		{
			if (roomIndex < 0 || roomIndex >= RoomGraph.MaxRooms)
				return false;
			var progress = _getRoomProgress(roomIndex);
			return progress.HoardSearched && progress.ChestsSearched && progress.IntelVisited;
		}

		// ===== Room search =====

		internal unsafe bool StartCurrentPlanRoomSearch(
			InstanceContentDeepDungeon* dd,
			FloorObjectEvidenceSnapshot objectEvidence,
			Vector3 playerPosition,
			out bool evidenceUnavailable,
			out RoomSearchChestDiagnostic? chestDiagnostic)
		{
			evidenceUnavailable = false;
			chestDiagnostic = null;
			var entry = CurrentPlanEntry;
			if (!entry.HasValue)
			{
				_roomContext = null;
				return false;
			}

			_roomContext = RoomSearchContext.BuildForRoom(
				dd,
				objectEvidence,
				entry.Value.RoomIndex,
				playerPosition,
				entry.Value.ShouldProbeHoard,
				entry.Value.ShouldSearchChests,
				entry.Value.ShouldVisitForIntel,
				entry.Value.HoardEvidenceState,
				_configSnapshot,
				_palacePalProvider,
				_detailedMap,
				_cachedHoardIndicatorPos,
				_observedSightTrapPositions,
				out evidenceUnavailable,
				out chestDiagnostic);
			if (_roomContext?.BlindFallbackUnavailable == true)
			{
				_blindHoardProbeSuppressedRooms.Add(entry.Value.RoomIndex);
				_roomPlan[0] = entry.Value with { ShouldProbeHoard = false };
				Service.Log.Info(
					$"[AutoPilot] Suppressed blind hoard probe in room {entry.Value.RoomIndex}: " +
					$"catalog={_roomContext.FallbackCatalogCandidateCount} " +
					$"palacePal={_roomContext.FallbackPalacePalCandidateCount} " +
					$"union={_roomContext.FallbackUnionCandidateCount}");
			}
			return _roomContext != null;
		}

		public unsafe bool StartBandedOnlyRoomSearch(InstanceContentDeepDungeon* dd, int roomIndex, Vector3 playerPosition, Vector3 bandedPosition)
		{
			_roomContext = RoomSearchContext.BuildBandedOnly(dd, roomIndex, playerPosition, bandedPosition);
			return _roomContext != null;
		}

		public void AdvanceWaypoint()
		{
			_roomContext?.AdvanceWaypoint();
		}

		// ===== Planning =====

		internal unsafe List<RoomPlanEntry> GeneratePlan(InstanceContentDeepDungeon* dd, NormalFloorGraphSnapshot normalGraph, ChatWatchers? chatWatchers, Vector3 playerPosition, bool nativeIntuitionActive)
		{
			_roomPlan.Clear();
			_retainedNonHoardRoute.Clear();

			var snapshot = BuildFloorPlanSnapshot(dd, normalGraph, chatWatchers, playerPosition, nativeIntuitionActive);
			if (!snapshot.HasValue)
			{
				_hasPlanningSnapshot = false;
				_lastPlanTrace = new FloorPlanTrace
				{
					Plan = Array.Empty<RoomPlanEntry>(),
					Candidates = Array.Empty<FloorPlanCandidateTrace>(),
					Selections = Array.Empty<FloorPlanSelectionTrace>(),
					RejectionReason = "floor snapshot unavailable"
				};
				if (dd != null)
				{
					Service.Log.Info("[AutoPilot] Player room unavailable for planning");
				}
				return _roomPlan;
			}

			var trace = PlanGenerator.GenerateTrace(snapshot.Value);
			var retainedTrace = PlanGenerator.GenerateTrace(snapshot.Value with
			{
				BandedEnabled = false,
				HoardOpenedThisFloor = true,
				CachedHoardIndicatorRoomIndex = null,
				IntuitionActive = false,
				ChatSaysHoard = false,
				ChatSaysNoHoard = false,
				InheritedNoHoardInferred = false,
				UsedIntuitionThisFloor = false
			});
			_hasPlanningSnapshot = true;
			_lastHoardEvidenceState = PlanGenerator.ResolveHoardEvidenceState(snapshot.Value);
			_lastPlanTrace = trace;
			_roomPlan.AddRange(trace.Plan);
			_retainedNonHoardRoute.AddRange(retainedTrace.Plan);

			Service.Log.Info($"[AutoPilot] Planned rooms: state={_lastHoardEvidenceState} {string.Join(" -> ", _roomPlan.Select(x => $"{x.RoomIndex}[P={(x.ShouldProbeHoard ? 1 : 0)} C={(x.ShouldSearchChests ? 1 : 0)} I={(x.ShouldVisitForIntel ? 1 : 0)}]"))}");
			return _roomPlan;
		}

		private unsafe FloorPlanSnapshot? BuildFloorPlanSnapshot(InstanceContentDeepDungeon* dd, NormalFloorGraphSnapshot normalGraph, ChatWatchers? chatWatchers, Vector3 playerPosition, bool nativeIntuitionActive)
		{
			if (dd == null)
				return null;

			var reachableRooms = normalGraph.ReachableRooms;
			if (reachableRooms.Count == 0)
				return null;

			int playerRoom = RoomGraph.GetLocalPlayerRoomIndex(dd);
			if (playerRoom < 0 || !reachableRooms.Contains(playerRoom))
			{
				return null;
			}

			DeepDungeonFloorsetTracker.TryGetCurrentFloorsetState(
				dd->Floor,
				out FloorsetHoardDistributionState floorsetState);
			int floorsetBandedCount = floorsetState.TotalHoardCount;
			FloorsetHoardOpportunity hoardOpportunity =
				FloorsetHoardDistributionPolicy.Decide(floorsetState, dd->Floor);

			int? cachedIndicatorRoomIndex = null;
			if (_cachedHoardIndicatorPos.HasValue)
			{
				for (int i = 0; i < reachableRooms.Count; i++)
				{
					if (IsPositionInsideRoom(dd, reachableRooms[i], _cachedHoardIndicatorPos.Value))
					{
						cachedIndicatorRoomIndex = reachableRooms[i];
						break;
					}
				}
			}

			var roomData = new PlanRoomData[reachableRooms.Count];
			for (int i = 0; i < reachableRooms.Count; i++)
			{
				int roomIndex = reachableRooms[i];
				var flags = dd->MapData[roomIndex];
				var progress = _getRoomProgress(roomIndex);
				roomData[i] = new PlanRoomData
				{
					RoomIndex = roomIndex,
					IsHome = (flags & InstanceContentDeepDungeon.RoomFlags.Home) != 0,
					IsSearched = IsRoomSearched(roomIndex),
					IsHoardSearched = progress.HoardSearched,
					BlindHoardProbeSuppressed =
						_blindHoardProbeSuppressedRooms.Contains(roomIndex),
					AreChestsSearched = progress.ChestsSearched,
					IsIntelVisited = progress.IntelVisited,
					IsRevealed = DeepDungeonChestData.IsRoomRevealed(dd, roomIndex),
					HasKnownChestEntry =
						DeepDungeonChestData.RoomHasKnownChestEntry(
							dd,
							roomIndex),
					HasEnabledChest = DeepDungeonChestData.RoomHasEnabledChest(dd, roomIndex, _configSnapshot)
				};
			}

			return new FloorPlanSnapshot
			{
				FloorNumber = dd->Floor,
				PlayerRoomIndex = playerRoom,
				PassageRoomIndex = RoomGraph.GetPassageRoomIndex(dd),
				ReachableRooms = reachableRooms.ToArray(),
				RoomDistances = normalGraph.RoomDistances,
				Rooms = roomData,
				BandedEnabled = _configSnapshot.BandedEnabled,
				OpenChestsEnabled = OpenChestsEnabled,
				HoardOpenedThisFloor = _hoardOpenedThisFloor,
				FloorsetBandedCount = floorsetBandedCount,
				FloorsetHoardOpportunity = hoardOpportunity,
				CachedHoardIndicatorRoomIndex = cachedIndicatorRoomIndex,
				IntuitionActive = nativeIntuitionActive,
				ChatSaysHoard = chatWatchers?.ChatSaysHoard ?? false,
				ChatSaysNoHoard = chatWatchers?.ChatSaysNoHoard ?? false,
				InheritedNoHoardInferred = _inheritedNoHoardInferred,
				UsedIntuitionThisFloor = chatWatchers?.HasCurrentFloorIntuitionUse ?? false
			};
		}

		private unsafe bool IsPositionInsideRoom(InstanceContentDeepDungeon* dd, int roomIndex, Vector3 position)
		{
			if (!MapPos.TryGetRoomCenter(dd, roomIndex, out var center))
			{
				return false;
			}

			float dx = position.X - center.X;
			float dz = position.Z - center.Z;
			return dx * dx + dz * dz <= 30f * 30f;
		}

		private static bool OptionsEqual(RunOptions left, RunOptions right)
		{
			return left.OpenGold == right.OpenGold &&
			       left.OpenSilver == right.OpenSilver &&
			       left.OpenBronze == right.OpenBronze &&
			       left.BandedEnabled == right.BandedEnabled &&
			       left.LeaveMode == right.LeaveMode &&
			       left.LeaveAfterMinutes == right.LeaveAfterMinutes;
		}

		// ===== Debug snapshot =====

		public sealed class AutoPilotDebugSnapshot
		{
			public List<int> RoomPath = new();
			public int CurrentRoomIdx;
			public HashSet<int> CompletedRooms = new();
			public HashSet<int> BlindHoardProbeSuppressedRooms = new();
			public FloorPhase Phase;
			public TaskPhase TaskPhase;
			public string Status = "";
			public int HoardCount;
			public HoardEvidenceState HoardEvidenceState;
			public Vector3? CachedHoardIndicatorPos;
			public string DetailedMapStatus = string.Empty;
			public List<RoomPlanDebugEntry> RoomPlan = new();
			public FloorPlanTraceDebugSnapshot? PlanTrace;
			public RoomContextSnapshot? RoomContext;
		}

		public sealed class RoomPlanDebugEntry
		{
			public int RoomIndex;
			public bool ShouldProbeHoard;
			public bool ShouldSearchChests;
			public bool ShouldVisitForIntel;
			public HoardEvidenceState HoardEvidenceState;
		}

		public sealed class FloorPlanTraceDebugSnapshot
		{
			public List<FloorPlanCandidateTrace> Candidates = new();
			public List<FloorPlanSelectionTrace> Selections = new();
			public string? RejectionReason;
		}

		public AutoPilotDebugSnapshot GetDebugSnapshot()
		{
			var completed = new HashSet<int>();
			for (int i = 0; i < RoomGraph.MaxRooms; i++)
			{
				var progress = _getRoomProgress(i);
				if (progress.HoardSearched || progress.ChestsSearched || progress.IntelVisited)
				{
					completed.Add(i);
				}
			}

			return new AutoPilotDebugSnapshot
			{
				RoomPath = _roomPlan.Select(x => x.RoomIndex).ToList(),
				CurrentRoomIdx = 0,
				CompletedRooms = completed,
				BlindHoardProbeSuppressedRooms =
					new HashSet<int>(_blindHoardProbeSuppressedRooms),
				HoardCount = _observedHoardCount,
				HoardEvidenceState = _lastHoardEvidenceState,
				CachedHoardIndicatorPos = _cachedHoardIndicatorPos,
				DetailedMapStatus = _detailedMap.Status,
				RoomPlan = _roomPlan.Select(x => new RoomPlanDebugEntry
				{
					RoomIndex = x.RoomIndex,
					ShouldProbeHoard = x.ShouldProbeHoard,
					ShouldSearchChests = x.ShouldSearchChests,
					ShouldVisitForIntel = x.ShouldVisitForIntel,
					HoardEvidenceState = x.HoardEvidenceState
				}).ToList(),
				PlanTrace = BuildPlanTraceDebugSnapshot(),
				RoomContext = _roomContext?.BuildDebugInfo()
			};
		}

		private FloorPlanTraceDebugSnapshot? BuildPlanTraceDebugSnapshot()
		{
			if (_lastPlanTrace.Candidates == null && _lastPlanTrace.Selections == null && _lastPlanTrace.RejectionReason == null)
				return null;

			return new FloorPlanTraceDebugSnapshot
			{
				Candidates = _lastPlanTrace.Candidates?.ToList() ?? new List<FloorPlanCandidateTrace>(),
				Selections = _lastPlanTrace.Selections?.ToList() ?? new List<FloorPlanSelectionTrace>(),
				RejectionReason = _lastPlanTrace.RejectionReason
			};
		}
	}
}
