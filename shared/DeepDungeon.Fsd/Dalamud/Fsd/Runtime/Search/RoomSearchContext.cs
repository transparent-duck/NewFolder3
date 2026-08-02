using System;
using System.Collections.Generic;
using System.Numerics;
using DeepDungeon.Fsd.Dalamud;
using DeepDungeon.Fsd.Core;
using CoreRoomSearchBuilder = DeepDungeon.Fsd.Core.RoomSearchBuilder;
using CoreRoomSearchSnapshot = DeepDungeon.Fsd.Core.RoomSearchSnapshot;
using CoreSnapshotTrapIndicatorEntry = DeepDungeon.Fsd.Core.SnapshotTrapIndicatorEntry;
using CoreRoomSearchWaypointEntry = DeepDungeon.Fsd.Core.RoomSearchWaypointEntry;
using CoreRoomSearchWaypointSource = DeepDungeon.Fsd.Core.RoomSearchWaypointSource;
using CoreSearchObjectiveType = DeepDungeon.Fsd.Core.SearchObjectiveType;
using CoreSnapshotChestEntry = DeepDungeon.Fsd.Core.SnapshotChestEntry;
using DeepDungeon.Fsd.Dalamud.GameState;
using DeepDungeon.Fsd.Dalamud.Map;
using DeepDungeon.Fsd.Dalamud.Runtime;
using DeepDungeon.Fsd.Dalamud.Runtime.Floor;
using global::Dalamud.Game.ClientState.Objects.Types;
using FFXIVClientStructs.FFXIV.Client.Game.InstanceContent;

namespace DeepDungeon.Fsd.Dalamud.Runtime.Search
{
	internal enum WaypointOutcomeKind
	{
		Completed,
		PolicySkipped,
		Failed,
		Deferred,
		Preempted
	}

	public enum RoomObjectiveType
	{
		Trap,
		ChestBronze,
		ChestSilver,
		ChestGold,
		ChestBanded
	}

	internal readonly record struct RoomWaypoint(
		Vector3 Position,
		RoomObjectiveType Type,
		float ArrivalRadius = 0f,
		bool HasExplicitArrivalRadius = false);

	internal sealed record NativeRoomChestDiagnostic(
		int TableIndex,
		byte ChestType,
		string ChestTypeName,
		sbyte RoomIndex,
		bool EnabledByRunOptions,
		bool StateAvailable,
		string State);

	internal sealed record ClassifiedChestDiagnostic(
		uint EntityId,
		ulong GameObjectId,
		uint BaseId,
		string Kind,
		string ObjectKind,
		bool IsTargetable,
		float X,
		float Y,
		float Z,
		float DistanceToRoomCenterXZ,
		bool AllowedKind,
		bool InsideRoom,
		bool Accepted,
		string RejectionReason,
		bool NativeStateAvailable,
		string NativeCompletionKind,
		string NativeState,
		string NativeFlags);

	internal sealed record RoomSearchChestDiagnostic(
		int RoomIndex,
		float RoomCenterX,
		float RoomCenterY,
		float RoomCenterZ,
		float RoomRadius,
		bool ShouldSearchChests,
		bool NativeRoomRevealed,
		IReadOnlyList<NativeRoomChestDiagnostic> NativeRoomChests,
		IReadOnlyList<ClassifiedChestDiagnostic> ClassifiedChests);

	/// <summary>
	/// Encapsulates per-room waypoint planning so AutoPilotExecutor can request the
	/// next objective without managing multiple queues.
	/// </summary>
	internal sealed class RoomSearchContext
	{
		private static long _lastSnapshotErrorAtMs;
		private readonly List<RoomWaypoint> _waypoints;
		private readonly float _roomRadius;
		private readonly bool _shouldProbeHoard;
		private readonly bool _shouldSearchChests;
		private readonly bool _shouldVisitForIntel;
		private readonly CategoryOutcome _hoardOutcome = new();
		private readonly CategoryOutcome _chestOutcome = new();
		private readonly CategoryOutcome _intelOutcome = new();
		private int _currentWaypointIdx;

		private readonly record struct VisibleChestSource(Vector3 Position, RoomObjectiveType Type);

		private sealed class LiveRoomSearchSourceData
		{
			public Vector3? VisibleBandedChestPosition;
			public IReadOnlyList<Vector3> DetailedMapCandidates = Array.Empty<Vector3>();
			public int DetailedMapDirectCandidateCount;
			public IReadOnlyList<Vector3> FallbackTrapMarkers = Array.Empty<Vector3>();
			public IReadOnlyList<CoreSnapshotTrapIndicatorEntry> VisibleTrapIndicators = Array.Empty<CoreSnapshotTrapIndicatorEntry>();
			public List<VisibleChestSource> VisibleChests { get; } = new();
		}

		private RoomSearchContext(
			int roomIndex,
			Vector3 roomCenter,
			float roomRadius,
			List<RoomWaypoint> orderedWaypoints,
			bool shouldProbeHoard,
			bool shouldSearchChests,
			bool shouldVisitForIntel)
		{
			RoomIndex = roomIndex;
			RoomCenter = roomCenter;
			_roomRadius = roomRadius;
			_waypoints = orderedWaypoints;
			_shouldProbeHoard = shouldProbeHoard;
			_shouldSearchChests = shouldSearchChests;
			_shouldVisitForIntel = shouldVisitForIntel;
			for (int i = 0; i < orderedWaypoints.Count; i++)
			{
				if (orderedWaypoints[i].Type is RoomObjectiveType.Trap or RoomObjectiveType.ChestBanded)
					_hoardOutcome.RequiredCount++;
				else
					_chestOutcome.RequiredCount++;
			}
			if (shouldVisitForIntel)
				_intelOutcome.RequiredCount = 1;
		}

		private sealed class CategoryOutcome
		{
			public int RequiredCount;
			public int CompletedCount;
			public int SkippedCount;
			public bool Failed;
			public bool Deferred;
			public bool Preempted;

			public void Record(WaypointOutcomeKind outcome)
			{
				switch (outcome)
				{
					case WaypointOutcomeKind.Completed:
						CompletedCount++;
						break;
					case WaypointOutcomeKind.PolicySkipped:
						SkippedCount++;
						break;
					case WaypointOutcomeKind.Failed:
						Failed = true;
						break;
					case WaypointOutcomeKind.Deferred:
						Deferred = true;
						break;
					case WaypointOutcomeKind.Preempted:
						Preempted = true;
						break;
				}
			}
		}

		public int RoomIndex { get; }
		public Vector3 RoomCenter { get; }
		public int CurrentWaypointIndex => _currentWaypointIdx;
		public int TotalWaypointCount => _waypoints.Count;
		public int RemainingWaypointCount => Math.Max(0, _waypoints.Count - _currentWaypointIdx);
		public bool HasWaypoints => _currentWaypointIdx < _waypoints.Count;
		public RoomWaypoint? CurrentWaypoint => _currentWaypointIdx < _waypoints.Count ? _waypoints[_currentWaypointIdx] : null;
		public IReadOnlyList<RoomWaypoint> Waypoints => _waypoints;
		public string LastOutcomeReason { get; private set; } = string.Empty;

		public void AdvanceWaypoint()
		{
			if (_currentWaypointIdx < _waypoints.Count)
			{
				_currentWaypointIdx++;
			}
		}

		public void RecordWaypointOutcome(RoomWaypoint waypoint, WaypointOutcomeKind outcome, string reason)
		{
			LastOutcomeReason = reason;
			if (waypoint.Type is RoomObjectiveType.Trap or RoomObjectiveType.ChestBanded)
				_hoardOutcome.Record(outcome);
			else
				_chestOutcome.Record(outcome);
		}

		public void MarkIntelCompleted()
		{
			if (_shouldVisitForIntel && _intelOutcome.CompletedCount == 0)
			{
				_intelOutcome.CompletedCount = 1;
				LastOutcomeReason = "IntelSettleCompleted";
			}
		}

		public RoomObjectiveOutcomeSnapshot BuildOutcomeSnapshot(bool authoritativeHoardResolved)
		{
			return new RoomObjectiveOutcomeSnapshot(
				BuildProgress(_shouldProbeHoard, _hoardOutcome, authoritativeHoardResolved),
				BuildProgress(_shouldSearchChests, _chestOutcome, authoritativelyResolved: false),
				BuildProgress(_shouldVisitForIntel, _intelOutcome, authoritativelyResolved: false));
		}

		private static ObjectiveCategoryProgress BuildProgress(bool requested, CategoryOutcome outcome, bool authoritativelyResolved)
		{
			return new ObjectiveCategoryProgress(
				requested,
				outcome.RequiredCount,
				outcome.CompletedCount,
				outcome.SkippedCount,
				authoritativelyResolved,
				outcome.Failed,
				outcome.Deferred,
				outcome.Preempted);
		}

		public bool Contains(Vector3 position)
		{
			var dx = position.X - RoomCenter.X;
			var dz = position.Z - RoomCenter.Z;
			return dx * dx + dz * dz <= _roomRadius * _roomRadius;
		}

		public RoomContextSnapshot BuildDebugInfo()
		{
			var snapshot = new RoomContextSnapshot
			{
				RoomIndex = RoomIndex,
				CurrentWaypointIndex = _currentWaypointIdx
			};

			foreach (var waypoint in _waypoints)
			{
				snapshot.Waypoints.Add(new RoomContextWaypoint
				{
					Position = waypoint.Position,
					Type = waypoint.Type
				});
			}

			return snapshot;
		}

		public static unsafe RoomSearchContext? BuildForRoom(
			InstanceContentDeepDungeon* dd,
			FloorObjectEvidenceSnapshot objectEvidence,
			int roomIndex,
			Vector3 playerPosition,
			bool shouldProbeHoard,
			bool shouldSearchChests,
			bool shouldVisitForIntel,
			HoardEvidenceState hoardEvidenceState,
			RunOptions config,
			PalacePalProvider palacePalProvider,
			DetailedMapRunSnapshot detailedMap,
			Vector3? cachedHoardIndicatorPos,
			IReadOnlyList<Vector3> observedSightTrapPositions,
			out bool evidenceUnavailable,
			out RoomSearchChestDiagnostic? chestDiagnostic)
		{
			evidenceUnavailable = false;
			chestDiagnostic = null;
			if (dd == null || !objectEvidence.Available)
			{
				evidenceUnavailable = true;
				return null;
			}

			if (!MapPos.TryGetRoomCenter(dd, roomIndex, out var center))
			{
				evidenceUnavailable = true;
				return null;
			}

			const float roomRadius = 30f;
			var snapshot = BuildRoomSearchSnapshot(
				dd,
				objectEvidence,
				roomIndex,
				shouldProbeHoard,
				shouldSearchChests,
				shouldVisitForIntel,
				hoardEvidenceState,
				config,
				palacePalProvider,
				detailedMap,
				cachedHoardIndicatorPos,
				observedSightTrapPositions,
				center,
				roomRadius,
				out var sourceData,
				out chestDiagnostic);
			if (snapshot == null)
			{
				evidenceUnavailable = true;
				return null;
			}

			var selected = CoreRoomSearchBuilder.Build(snapshot.Value);
			var ordered = ResolveWaypoints(
				playerPosition,
				selected.Waypoints,
				sourceData,
				cachedHoardIndicatorPos);
			return new RoomSearchContext(
				roomIndex,
				center,
				roomRadius,
				ordered,
				shouldProbeHoard,
				shouldSearchChests,
				shouldVisitForIntel);
		}

		public static unsafe RoomSearchContext? BuildBandedOnly(
			InstanceContentDeepDungeon* dd,
			int roomIndex,
			Vector3 playerPosition,
			Vector3 bandedPosition)
		{
			if (dd == null)
				return null;

			if (!MapPos.TryGetRoomCenter(dd, roomIndex, out var center))
			{
				return null;
			}

			const float roomRadius = 30f;
			var ordered = new List<RoomWaypoint>
			{
				new RoomWaypoint(bandedPosition, RoomObjectiveType.ChestBanded)
			};
			return new RoomSearchContext(
				roomIndex,
				center,
				roomRadius,
				ordered,
				shouldProbeHoard: true,
				shouldSearchChests: false,
				shouldVisitForIntel: false);
		}

		private static unsafe CoreRoomSearchSnapshot? BuildRoomSearchSnapshot(
			InstanceContentDeepDungeon* dd,
			FloorObjectEvidenceSnapshot objectEvidence,
			int roomIndex,
			bool shouldProbeHoard,
			bool shouldSearchChests,
			bool shouldVisitForIntel,
			HoardEvidenceState hoardEvidenceState,
			RunOptions config,
			PalacePalProvider palacePalProvider,
			DetailedMapRunSnapshot detailedMap,
			Vector3? cachedHoardIndicatorPos,
			IReadOnlyList<Vector3> observedSightTrapPositions,
			Vector3 roomCenter,
			float roomRadius,
			out LiveRoomSearchSourceData sourceData,
			out RoomSearchChestDiagnostic chestDiagnostic)
		{
			sourceData = new LiveRoomSearchSourceData();
			var nativeRoomChests = BuildNativeRoomChestDiagnostics(dd, roomIndex, config);
			var classifiedChests = new List<ClassifiedChestDiagnostic>(objectEvidence.Chests.Count);
			chestDiagnostic = new RoomSearchChestDiagnostic(
				roomIndex,
				roomCenter.X,
				roomCenter.Y,
				roomCenter.Z,
				roomRadius,
				shouldSearchChests,
				DeepDungeonChestData.IsRoomRevealed(dd, roomIndex),
				nativeRoomChests,
				classifiedChests);
			bool visibleBandedChestInRoom = false;
			bool cachedHoardIndicatorInRoom = false;
			bool hasDetailedMapRoom = false;
			int detailedMapCandidateCount = 0;
			int fallbackTrapCount = 0;
			IReadOnlyList<CoreSnapshotChestEntry> visibleChests = Array.Empty<CoreSnapshotChestEntry>();
			IReadOnlyList<CoreSnapshotTrapIndicatorEntry> visibleTrapIndicators = Array.Empty<CoreSnapshotTrapIndicatorEntry>();

			try
			{
				var visibleChestEntries = shouldSearchChests
					? new List<CoreSnapshotChestEntry>()
					: null;
				for (int i = 0; i < objectEvidence.Chests.Count; i++)
				{
					var chest = objectEvidence.Chests[i];
					bool allowedKind = IsChestAllowed(chest.Kind, config);
					bool insideRoom = IsInsideRoom(chest.Object.Position, roomCenter, roomRadius);
					bool accepted = shouldSearchChests &&
					                chest.Object.IsTargetable &&
					                allowedKind &&
					                insideRoom;
					string rejectionReason = accepted
						? "None"
						: !shouldSearchChests
							? "RoomPlanDoesNotRequestChests"
							: !chest.Object.IsTargetable
								? "NotTargetable"
								: !allowedKind
									? "ChestKindDisabled"
									: "OutsideTargetRoom";
					classifiedChests.Add(new ClassifiedChestDiagnostic(
						chest.Object.EntityId,
						chest.Object.GameObjectId,
						chest.Object.BaseId,
						chest.Kind.ToString(),
						chest.Object.ObjectKind,
						chest.Object.IsTargetable,
						chest.Object.Position.X,
						chest.Object.Position.Y,
						chest.Object.Position.Z,
						DistanceXZ(chest.Object.Position, roomCenter),
						allowedKind,
						insideRoom,
						accepted,
						rejectionReason,
						chest.NativeStateAvailable,
						chest.NativeCompletionKind.ToString(),
						chest.State.ToString(),
						chest.Flags.ToString()));
					if (!accepted)
						continue;

					var type = ClassifyChest(chest.Kind);
					sourceData.VisibleChests.Add(new VisibleChestSource(chest.Object.Position, type));
					visibleChestEntries!.Add(new CoreSnapshotChestEntry
					{
						Type = MapObjectiveType(type)
					});
				}
				if (visibleChestEntries != null)
					visibleChests = visibleChestEntries;

				if (shouldProbeHoard)
				{
					if (!BandedChestLocator.TryFindNearestAround(objectEvidence, roomCenter, roomRadius, out var visibleBanded))
						return null;

					if (visibleBanded.HasValue)
					{
						sourceData.VisibleBandedChestPosition = visibleBanded.Value;
						visibleBandedChestInRoom = true;
					}

					cachedHoardIndicatorInRoom = cachedHoardIndicatorPos.HasValue &&
					                             IsInsideRoom(cachedHoardIndicatorPos.Value, roomCenter, roomRadius);

					if (detailedMap.Policy == DetailedMapRuntimePolicy.DetailedMap)
					{
						DetailedMapCatalog catalog = detailedMap.Catalog ??
							throw new InvalidDataException(
								"Detailed-map policy has no run-scoped catalog.");
						catalog.TryGetRoom(
							dd->ActiveLayoutIndex,
							roomIndex,
							out DetailedMapCatalogRoom catalogRoom);
						BuildDetailedMapCandidateOrder(
							catalog,
							catalogRoom,
							dd->Floor,
							objectEvidence,
							observedSightTrapPositions,
							roomCenter,
							roomRadius,
							out IReadOnlyList<Vector3> detailedMapCandidates,
							out int directCandidateCount,
							out bool usePalacePalFallback,
							out visibleTrapIndicators);
						if (usePalacePalFallback)
						{
							sourceData.FallbackTrapMarkers =
								palacePalProvider.GetCandidatePositionsForRoom(
									dd,
									roomIndex);
							fallbackTrapCount =
								sourceData.FallbackTrapMarkers.Count;
						}
						else
						{
							hasDetailedMapRoom = true;
							sourceData.DetailedMapCandidates = detailedMapCandidates;
							sourceData.DetailedMapDirectCandidateCount =
								directCandidateCount;
							sourceData.VisibleTrapIndicators =
								visibleTrapIndicators;
							detailedMapCandidateCount = detailedMapCandidates.Count;
						}
					}
					else
					{
						sourceData.FallbackTrapMarkers = palacePalProvider.GetCandidatePositionsForRoom(dd, roomIndex);
						fallbackTrapCount = sourceData.FallbackTrapMarkers.Count;
					}
				}

			}
			catch (Exception ex)
			{
				long nowMs = Environment.TickCount64;
				if (_lastSnapshotErrorAtMs == 0 || nowMs - _lastSnapshotErrorAtMs >= 2000)
				{
					_lastSnapshotErrorAtMs = nowMs;
					Service.Log.Warning($"[RoomContext] Failed to build room-search snapshot: {ex.Message}");
				}
				return null;
			}

			return new CoreRoomSearchSnapshot
			{
				RoomIndex = roomIndex,
				ShouldSearchHoard = shouldProbeHoard,
				ShouldProbeHoard = shouldProbeHoard,
				ShouldSearchChests = shouldSearchChests,
				ShouldVisitForIntel = shouldVisitForIntel,
				HoardEvidenceState = hoardEvidenceState,
				VisibleBandedChestInRoom = visibleBandedChestInRoom,
				CachedHoardIndicatorInRoom = cachedHoardIndicatorInRoom,
				HasDetailedMapRoom = hasDetailedMapRoom,
				DetailedMapCandidateCount = detailedMapCandidateCount,
				FallbackTrapCount = fallbackTrapCount,
				VisibleChests = visibleChests,
				VisibleTrapIndicators = visibleTrapIndicators
			};
		}

		private static List<RoomWaypoint> ResolveWaypoints(
			Vector3 playerPosition,
			IReadOnlyList<CoreRoomSearchWaypointEntry> selectedWaypoints,
			LiveRoomSearchSourceData sourceData,
			Vector3? cachedHoardIndicatorPos)
		{
			var exact = new List<RoomWaypoint>(1);
			var direct = new List<RoomWaypoint>(sourceData.DetailedMapDirectCandidateCount);
			var ordinary = new List<RoomWaypoint>(selectedWaypoints.Count);
			var detailedOrdinary =
				new List<RoomWaypoint>(sourceData.DetailedMapCandidates.Count);
			for (int i = 0; i < selectedWaypoints.Count; i++)
			{
				var waypoint = selectedWaypoints[i];
				switch (waypoint.Source)
				{
					case CoreRoomSearchWaypointSource.VisibleBandedChest:
						if (sourceData.VisibleBandedChestPosition.HasValue)
						{
							exact.Add(new RoomWaypoint(sourceData.VisibleBandedChestPosition.Value, RoomObjectiveType.ChestBanded));
						}
						break;

					case CoreRoomSearchWaypointSource.CachedHoardIndicator:
						if (cachedHoardIndicatorPos.HasValue)
						{
							exact.Add(new RoomWaypoint(cachedHoardIndicatorPos.Value, RoomObjectiveType.Trap));
						}
						break;

					case CoreRoomSearchWaypointSource.DetailedMapCandidate:
						if ((uint)waypoint.SourceIndex < sourceData.DetailedMapCandidates.Count)
						{
							var resolved = new RoomWaypoint(
								sourceData.DetailedMapCandidates[waypoint.SourceIndex],
								RoomObjectiveType.Trap);
							if (waypoint.SourceIndex < sourceData.DetailedMapDirectCandidateCount)
								direct.Add(resolved);
							else
								detailedOrdinary.Add(resolved);
						}
						break;

					case CoreRoomSearchWaypointSource.FallbackTrap:
						if ((uint)waypoint.SourceIndex < sourceData.FallbackTrapMarkers.Count)
						{
							ordinary.Add(new RoomWaypoint(sourceData.FallbackTrapMarkers[waypoint.SourceIndex], RoomObjectiveType.Trap));
						}
						break;

					case CoreRoomSearchWaypointSource.VisibleChest:
						if ((uint)waypoint.SourceIndex < sourceData.VisibleChests.Count)
						{
							var chest = sourceData.VisibleChests[waypoint.SourceIndex];
							ordinary.Add(new RoomWaypoint(chest.Position, chest.Type));
						}
						break;
				}
			}

			return OrderWaypointsByEvidenceTier(
				playerPosition,
				exact,
				direct,
				detailedOrdinary,
				ordinary);
		}

		private static void BuildDetailedMapCandidateOrder(
			DetailedMapCatalog catalog,
			DetailedMapCatalogRoom? catalogRoom,
			byte floor,
			FloorObjectEvidenceSnapshot objectEvidence,
			IReadOnlyList<Vector3> observedSightTrapPositions,
			Vector3 roomCenter,
			float roomRadius,
			out IReadOnlyList<Vector3> candidates,
			out int directCandidateCount,
			out bool usePalacePalFallback,
			out IReadOnlyList<CoreSnapshotTrapIndicatorEntry> debugTrapIndicators)
		{
			if (catalogRoom == null)
			{
				DetailedMapRoomCandidatePlan missingRoomPlan =
					DetailedMapRoomCandidatePlanner.BuildPriorityOrder(
						catalog,
						null,
						floor,
						Array.Empty<RawWorldPosition>());
				candidates = Array.Empty<Vector3>();
				directCandidateCount = 0;
				usePalacePalFallback =
					missingRoomPlan.UsePalacePalFallback;
				debugTrapIndicators =
					Array.Empty<CoreSnapshotTrapIndicatorEntry>();
				return;
			}

			var observedTraps = new List<RawWorldPosition>();
			for (int index = 0; index < observedSightTrapPositions.Count; index++)
			{
				Vector3 position = observedSightTrapPositions[index];
				if (IsInsideRoom(position, roomCenter, roomRadius))
					observedTraps.Add(new RawWorldPosition(position.X, position.Y, position.Z));
			}
			var debugIndicators = new List<CoreSnapshotTrapIndicatorEntry>();
			for (int indicatorIndex = 0; indicatorIndex < objectEvidence.SightTrapIndicators.Count; indicatorIndex++)
			{
				var obj = objectEvidence.SightTrapIndicators[indicatorIndex];
				bool insideRoom = IsInsideRoom(obj.Position, roomCenter, roomRadius);
				var raw = new RawWorldPosition(obj.Position.X, obj.Position.Y, obj.Position.Z);
				int match = insideRoom
					? DetailedMapRoomCandidatePlanner.FindUniqueCandidate(
						catalogRoom.Candidates,
						raw)
					: -1;
				float? matchDistance = match >= 0
					? MathF.Sqrt(DistanceSquared(catalogRoom.Candidates[match].Position, raw))
					: null;
				debugIndicators.Add(new CoreSnapshotTrapIndicatorEntry
				{
					BaseId = obj.BaseId,
					IsInsideRoom = insideRoom,
					X = obj.Position.X,
					Y = obj.Position.Y,
					Z = obj.Position.Z,
					MatchedSlotIndex = match >= 0 ? match : null,
					MatchedDistance = matchDistance,
					MatchMethod = match >= 0 ? "Unique3d0.1" : "None"
				});
			}
			debugTrapIndicators = debugIndicators;
			DetailedMapRoomCandidatePlan candidatePlan =
				DetailedMapRoomCandidatePlanner.BuildPriorityOrder(
					catalog,
					catalogRoom,
					floor,
					observedTraps);
			int candidateCount =
				candidatePlan.DirectCandidates.Count +
				candidatePlan.OrdinaryCandidates.Count;
			var worldPositions = new Vector3[candidateCount];
			int outputIndex = 0;
			void Append(IReadOnlyList<DetailedMapRoomCandidate> source)
			{
				for (int index = 0; index < source.Count; index++)
				{
					DetailedMapRoomCandidate candidate = source[index];
					worldPositions[outputIndex] = new Vector3(
						candidate.Position.X,
						candidate.Position.Y,
						candidate.Position.Z);
					outputIndex++;
				}
			}
			Append(candidatePlan.DirectCandidates);
			Append(candidatePlan.OrdinaryCandidates);
			candidates = worldPositions;
			directCandidateCount = candidatePlan.DirectCandidates.Count;
			usePalacePalFallback = candidatePlan.UsePalacePalFallback;
		}

		internal static bool IsSightTrapIndicatorBaseId(uint baseId)
		{
			return (baseId >= 2007182 && baseId <= 2007186) || baseId == 2009504;
		}

		private static unsafe IReadOnlyList<NativeRoomChestDiagnostic> BuildNativeRoomChestDiagnostics(
			InstanceContentDeepDungeon* dd,
			int roomIndex,
			RunOptions config)
		{
			var result = new List<NativeRoomChestDiagnostic>();
			if (dd == null)
				return result;

			var chests = dd->Chests;
			for (int i = 0; i < chests.Length; i++)
			{
				var chest = chests[i];
				if (chest.ChestType == 0 || chest.RoomIndex != roomIndex)
					continue;

				result.Add(new NativeRoomChestDiagnostic(
					i,
					chest.ChestType,
					DescribeNativeChestType(chest.ChestType),
					chest.RoomIndex,
					DeepDungeonChestData.IsEnabledChestType(chest.ChestType, config),
					StateAvailable: false,
					State: "NotExposedByNativeChestTable"));
			}

			return result;
		}

		private static string DescribeNativeChestType(byte chestType)
		{
			return chestType switch
			{
				0 => "Empty",
				1 => "Bronze",
				2 => "Silver",
				3 => "Gold",
				_ => "Unknown"
			};
		}

		private static float DistanceXZ(Vector3 left, Vector3 right)
		{
			float dx = left.X - right.X;
			float dz = left.Z - right.Z;
			return MathF.Sqrt(dx * dx + dz * dz);
		}

		private static float DistanceSquared(in RawWorldPosition left, in RawWorldPosition right)
		{
			float dx = left.X - right.X;
			float dy = left.Y - right.Y;
			float dz = left.Z - right.Z;
			return dx * dx + dy * dy + dz * dz;
		}

		private static bool IsInsideRoom(Vector3 position, Vector3 center, float radius)
		{
			var dx = position.X - center.X;
			var dz = position.Z - center.Z;
			return dx * dx + dz * dz <= radius * radius;
		}

		private static bool IsChestAllowed(FloorChestKind kind, RunOptions config)
		{
			return kind switch
			{
				FloorChestKind.Gold => config.OpenGold,
				FloorChestKind.Silver => config.OpenSilver,
				FloorChestKind.Bronze => config.OpenBronze,
				_ => false
			};
		}

		private static RoomObjectiveType ClassifyChest(FloorChestKind kind)
		{
			return kind switch
			{
				FloorChestKind.Silver => RoomObjectiveType.ChestSilver,
				FloorChestKind.Gold => RoomObjectiveType.ChestGold,
				_ => RoomObjectiveType.ChestBronze
			};
		}

		private static CoreSearchObjectiveType MapObjectiveType(RoomObjectiveType type)
		{
			return type switch
			{
				RoomObjectiveType.Trap => CoreSearchObjectiveType.Trap,
				RoomObjectiveType.ChestBronze => CoreSearchObjectiveType.ChestBronze,
				RoomObjectiveType.ChestSilver => CoreSearchObjectiveType.ChestSilver,
				RoomObjectiveType.ChestGold => CoreSearchObjectiveType.ChestGold,
				RoomObjectiveType.ChestBanded => CoreSearchObjectiveType.ChestBanded,
				_ => CoreSearchObjectiveType.Trap
			};
		}

		internal static List<RoomWaypoint> OrderWaypointsByEvidenceTier(
			Vector3 start,
			IReadOnlyList<RoomWaypoint> exact,
			IReadOnlyList<RoomWaypoint> direct,
			IReadOnlyList<RoomWaypoint> ordinary)
		{
			var ordered = new List<RoomWaypoint>(exact.Count + direct.Count + ordinary.Count);
			Vector3 current = start;
			AppendNearestNeighborTier(exact, ordered, ref current);
			(List<RoomWaypoint> mergedDirect, List<RoomWaypoint> mergedOrdinary) =
				MergeOverlappingProbeTiers(direct, ordinary);
			AppendNearestNeighborTier(
				mergedDirect,
				ordered,
				ref current);
			AppendNearestNeighborTier(
				mergedOrdinary,
				ordered,
				ref current);
			return ordered;
		}

		private static List<RoomWaypoint> OrderWaypointsByEvidenceTier(
			Vector3 start,
			IReadOnlyList<RoomWaypoint> exact,
			IReadOnlyList<RoomWaypoint> direct,
			IReadOnlyList<RoomWaypoint> detailedOrdinary,
			IReadOnlyList<RoomWaypoint> ordinary)
		{
			var ordered = new List<RoomWaypoint>(
				exact.Count +
				direct.Count +
				detailedOrdinary.Count +
				ordinary.Count);
			Vector3 current = start;
			AppendNearestNeighborTier(exact, ordered, ref current);
			List<RoomWaypoint> mergedDirect =
				MergeOverlappingProbeWaypoints(direct);
			List<RoomWaypoint> mergedDetailedOrdinary =
				MergeOverlappingProbeWaypoints(detailedOrdinary);
			List<RoomWaypoint> mergedOrdinary =
				MergeOverlappingProbeWaypoints(ordinary);
			(mergedDirect, mergedDetailedOrdinary) =
				MergeOverlappingProbeTiers(mergedDirect, mergedDetailedOrdinary);
			(mergedDirect, mergedOrdinary) =
				MergeOverlappingProbeTiers(mergedDirect, mergedOrdinary);
			(mergedDetailedOrdinary, mergedOrdinary) =
				MergeOverlappingProbeTiers(mergedDetailedOrdinary, mergedOrdinary);
			AppendNearestNeighborTier(
				mergedDirect,
				ordered,
				ref current);

			if (mergedDetailedOrdinary.Count > 0)
			{
				ordered.AddRange(
					OrderFixedProbeOpenRoute(
						current,
						mergedDetailedOrdinary,
						mergedOrdinary));
			}
			else
			{
				AppendNearestNeighborTier(
					mergedOrdinary,
					ordered,
					ref current);
			}
			return ordered;
		}

		internal const float TrapMarkerTriggerRadius = 1.7f;
		private const float ProbeArrivalSafetyMargin = 0.01f;
		/// <summary>
		/// Minimum shared-probe arrival radius after ProbeArrivalSafetyMargin.
		/// Matches vnavmesh FollowPath's default waypoint Tolerance (0.25f);
		/// SimpleMove.PathfindAndMoveTo uses range=0, so that tolerance governs
		/// final-waypoint advancement. Smaller midpoints are not reliably reachable.
		/// </summary>
		private const float MinimumSharedProbeArrivalRadius = 0.25f;

		/// <summary>
		/// Replaces the first deterministic overlapping pair of trap probes with
		/// their XZ midpoint. The computed arrival radius is bounded by the true
		/// marker radius for both original footprints; chests are never merged.
		/// </summary>
		internal static List<RoomWaypoint> MergeOverlappingProbeWaypoints(
			IReadOnlyList<RoomWaypoint> waypoints)
		{
			var result = new List<RoomWaypoint>(waypoints);
			for (int firstIndex = 0; firstIndex < result.Count; firstIndex++)
			{
				if (result[firstIndex].Type != RoomObjectiveType.Trap)
					continue;

				for (int secondIndex = firstIndex + 1;
					 secondIndex < result.Count;
					 secondIndex++)
				{
					if (result[secondIndex].Type != RoomObjectiveType.Trap)
						continue;

					RoomWaypoint first = result[firstIndex];
					RoomWaypoint second = result[secondIndex];
					if (!TryCreateSharedProbe(first, second, out RoomWaypoint shared))
						continue;
					result[firstIndex] = shared;
					result.RemoveAt(secondIndex);
					return result;
				}
			}

			return result;
		}

		private static (List<RoomWaypoint> Higher, List<RoomWaypoint> Lower)
			MergeOverlappingProbeTiers(
				IReadOnlyList<RoomWaypoint> higher,
				IReadOnlyList<RoomWaypoint> lower)
		{
			List<RoomWaypoint> mergedHigher =
				MergeOverlappingProbeWaypoints(higher);
			List<RoomWaypoint> mergedLower =
				MergeOverlappingProbeWaypoints(lower);
			for (int higherIndex = 0;
				 higherIndex < mergedHigher.Count;
				 higherIndex++)
			{
				if (mergedHigher[higherIndex].Type != RoomObjectiveType.Trap)
					continue;
				for (int lowerIndex = 0;
					 lowerIndex < mergedLower.Count;
					 lowerIndex++)
				{
					if (mergedLower[lowerIndex].Type != RoomObjectiveType.Trap ||
						!TryCreateSharedProbe(
							mergedHigher[higherIndex],
							mergedLower[lowerIndex],
							out RoomWaypoint shared))
					{
						continue;
					}

					mergedHigher[higherIndex] = shared;
					mergedLower.RemoveAt(lowerIndex);
					return (mergedHigher, mergedLower);
				}
			}

			return (mergedHigher, mergedLower);
		}

		private static bool TryCreateSharedProbe(
			RoomWaypoint first,
			RoomWaypoint second,
			out RoomWaypoint shared)
		{
			shared = default;
			if (first.Type != RoomObjectiveType.Trap ||
				second.Type != RoomObjectiveType.Trap ||
				first.HasExplicitArrivalRadius ||
				second.HasExplicitArrivalRadius)
			{
				return false;
			}

			float dx = first.Position.X - second.Position.X;
			float dz = first.Position.Z - second.Position.Z;
			float distance = MathF.Sqrt(dx * dx + dz * dz);
			if (!float.IsFinite(distance) ||
				distance >= TrapMarkerTriggerRadius * 2f)
			{
				return false;
			}

			Vector3 midpoint = new(
				(first.Position.X + second.Position.X) * 0.5f,
				(first.Position.Y + second.Position.Y) * 0.5f,
				(first.Position.Z + second.Position.Z) * 0.5f);
			float arrivalRadius =
				TrapMarkerTriggerRadius - distance * 0.5f - ProbeArrivalSafetyMargin;
			if (arrivalRadius < MinimumSharedProbeArrivalRadius)
				return false;

			shared = new RoomWaypoint(
				midpoint,
				RoomObjectiveType.Trap,
				arrivalRadius,
				HasExplicitArrivalRadius: true);
			return true;
		}

		internal static List<RoomWaypoint> OrderFixedProbeOpenRoute(
			Vector3 start,
			IReadOnlyList<RoomWaypoint> fixedProbes,
			IReadOnlyList<RoomWaypoint> optionalWaypoints)
		{
			(List<RoomWaypoint> mergedFixedProbes,
				List<RoomWaypoint> mergedOptionalWaypoints) =
				MergeOverlappingProbeTiers(fixedProbes, optionalWaypoints);
			int fixedCount = mergedFixedProbes.Count;
			RoomWaypoint[] optional = mergedOptionalWaypoints
				.OrderBy(waypoint => waypoint.Position.X)
				.ThenBy(waypoint => waypoint.Position.Y)
				.ThenBy(waypoint => waypoint.Position.Z)
				.ThenBy(waypoint => waypoint.Type)
				.ToArray();
			int waypointCount = fixedCount + optional.Length;
			if (waypointCount == 0)
				return new List<RoomWaypoint>();
			if (waypointCount > 63)
			{
				throw new InvalidOperationException(
					"Fixed detailed-map room routing supports at most 63 waypoints.");
			}

			var waypoints = new RoomWaypoint[waypointCount];
			for (int index = 0; index < fixedCount; index++)
				waypoints[index] = mergedFixedProbes[index];
			for (int index = 0; index < optional.Length; index++)
				waypoints[fixedCount + index] = optional[index];

			var frontier = new Dictionary<(ulong Mask, int Last), RouteState>
			{
				[(0, -1)] = new RouteState(0f, [])
			};
			for (int step = 0; step < waypointCount; step++)
			{
				var nextFrontier =
					new Dictionary<(ulong Mask, int Last), RouteState>();
				foreach (KeyValuePair<(ulong Mask, int Last), RouteState> entry
				         in frontier)
				{
					int nextFixedIndex = 0;
					while (nextFixedIndex < fixedCount &&
					       (entry.Key.Mask & (1UL << nextFixedIndex)) != 0)
					{
						nextFixedIndex++;
					}

					if (nextFixedIndex < fixedCount)
					{
						TryExtendRoute(
							start,
							waypoints,
							entry.Key,
							entry.Value,
							nextFixedIndex,
							nextFrontier);
					}

					for (int optionalIndex = fixedCount;
					     optionalIndex < waypointCount;
					     optionalIndex++)
					{
						if ((entry.Key.Mask & (1UL << optionalIndex)) != 0)
							continue;
						TryExtendRoute(
							start,
							waypoints,
							entry.Key,
							entry.Value,
							optionalIndex,
							nextFrontier);
					}
				}

				frontier = nextFrontier;
			}

			RouteState? best = null;
			foreach (RouteState state in frontier.Values)
			{
				if (best == null ||
				    IsBetterRoute(state, best))
				{
					best = state;
				}
			}

			return best?.Path
				.Select(index => waypoints[index])
				.ToList() ?? new List<RoomWaypoint>();
		}

		private static void TryExtendRoute(
			Vector3 start,
			IReadOnlyList<RoomWaypoint> waypoints,
			(ulong Mask, int Last) key,
			RouteState state,
			int nextIndex,
			Dictionary<(ulong Mask, int Last), RouteState> nextFrontier)
		{
			Vector3 current = key.Last >= 0
				? waypoints[key.Last].Position
				: start;
			float cost =
				state.Cost +
				Vector3.Distance(current, waypoints[nextIndex].Position);
			var path = new int[state.Path.Length + 1];
			state.Path.CopyTo(path, 0);
			path[^1] = nextIndex;
			var candidate = new RouteState(cost, path);
			var nextKey = (key.Mask | (1UL << nextIndex), nextIndex);
			if (!nextFrontier.TryGetValue(nextKey, out RouteState? existing) ||
			    IsBetterRoute(candidate, existing))
			{
				nextFrontier[nextKey] = candidate;
			}
		}

		private static bool IsBetterRoute(RouteState candidate, RouteState existing)
		{
			if (candidate.Cost != existing.Cost)
				return candidate.Cost < existing.Cost;

			for (int index = 0; index < candidate.Path.Length; index++)
			{
				if (candidate.Path[index] != existing.Path[index])
					return candidate.Path[index] < existing.Path[index];
			}
			return false;
		}

		private sealed record RouteState(float Cost, int[] Path);

		private static void AppendNearestNeighborTier(
			IReadOnlyList<RoomWaypoint> inputs,
			List<RoomWaypoint> ordered,
			ref Vector3 current)
		{
			if (inputs.Count == 0)
				return;

			var remaining = new List<RoomWaypoint>(inputs);
			while (remaining.Count > 0)
			{
				var bestIdx = 0;
				var bestDist = float.MaxValue;

				for (int i = 0; i < remaining.Count; i++)
				{
					var d2 = Vector3.DistanceSquared(current, remaining[i].Position);
					if (d2 < bestDist)
					{
						bestDist = d2;
						bestIdx = i;
					}
				}

				var next = remaining[bestIdx];
				ordered.Add(next);
				current = next.Position;
				remaining.RemoveAt(bestIdx);
			}
		}
	}

	public sealed class RoomContextSnapshot
	{
		public int RoomIndex { get; init; }
		public int CurrentWaypointIndex { get; init; }
		public List<RoomContextWaypoint> Waypoints { get; init; } = new();
	}

	public sealed class RoomContextWaypoint
	{
		public Vector3 Position { get; init; }
		public RoomObjectiveType Type { get; init; }
	}
}
