using System;
using System.Collections.Generic;
using System.Numerics;
using DeepDungeon.Fsd.Core;
using DeepDungeon.Fsd.Dalamud.GameState;
using DeepDungeon.Fsd.Dalamud.Runtime.Search;
using FFXIVClientStructs.FFXIV.Client.Game.Object;

namespace DeepDungeon.Fsd.Dalamud.Runtime.Floor
{
	internal enum FloorChestKind
	{
		Bronze,
		Silver,
		Gold,
		Banded
	}

	internal readonly record struct FloorObjectEvidence(
		ushort ObjectIndex,
		ulong GameObjectId,
		uint EntityId,
		uint BaseId,
		Vector3 Position,
		bool IsTargetable,
		string ObjectKind,
		byte NativeCurrentDistance);

	internal readonly record struct FloorChestEvidence(
		FloorObjectEvidence Object,
		FloorChestKind Kind,
		bool NativeStateAvailable,
		NativeTreasureCompletionKind NativeCompletionKind,
		Treasure.TreasureState State,
		Treasure.TreasureFlags Flags);

	internal readonly record struct FloorHoardIndicatorEvidence(
		FloorObjectEvidence Object,
		string Name,
		string ObjectKind,
		byte SubKind,
		string Address);

	internal sealed record FloorObjectEvidenceSnapshot(
		bool Available,
		long Version,
		long RefreshSequence,
		DateTime CapturedAtUtc,
		Vector3? PlayerPosition,
		int ScannedObjectCount,
		IReadOnlyList<FloorChestEvidence> Chests,
		IReadOnlyList<FloorHoardIndicatorEvidence> HoardIndicators,
		IReadOnlyList<FloorObjectEvidence> SightTrapIndicators,
		IReadOnlyList<FloorObjectEvidence> PassageActors);

	internal readonly record struct FloorObjectEvidenceRefreshResult(
		bool Attempted,
		bool WasInvalidated,
		bool MaterialChanged,
		bool ScanCompleted,
		FloorObjectEvidenceSnapshot? Snapshot);

	internal sealed class FloorObjectEvidenceTracker : IDisposable
	{
		private const long RefreshIntervalMs = 100;
		private long _lastRefreshAtMs;
		private long _invalidationVersion;
		private long _consumedInvalidationVersion;
		private long _lastErrorAtMs;

		public FloorObjectEvidenceSnapshot? Current { get; private set; }
		public long RefreshCount { get; private set; }
		public long FullScanCount { get; private set; }
		public long InvalidationCount => _invalidationVersion;
		public bool IsDisposed { get; private set; }

		public void Invalidate()
		{
			if (!IsDisposed)
				_invalidationVersion++;
		}

		public unsafe FloorObjectEvidenceRefreshResult RefreshIfDue()
		{
			if (IsDisposed)
				return default;

			long nowMs = Environment.TickCount64;
			var decision = FloorObjectEvidenceRefreshPlanner.Decide(new FloorObjectEvidenceRefreshSnapshot(
				nowMs,
				_lastRefreshAtMs,
				_invalidationVersion,
				_consumedInvalidationVersion,
				Current != null,
				RefreshIntervalMs));
			if (!decision.ShouldRefresh)
				return default;

			_lastRefreshAtMs = nowMs;
			_consumedInvalidationVersion = _invalidationVersion;
			RefreshCount++;
			int scannedObjectCount = 0;
			Vector3? playerPosition = Service.LocalPlayer?.Position;
			try
			{
				var chests = new List<FloorChestEvidence>();
				var hoardIndicators = new List<FloorHoardIndicatorEvidence>();
				var sightTrapIndicators = new List<FloorObjectEvidence>();
				var passageActors = new List<FloorObjectEvidence>();
				foreach (var obj in Service.GameObjects)
				{
					if (obj == null)
						continue;
					scannedObjectCount++;
					var evidence = new FloorObjectEvidence(
						obj.ObjectIndex,
						obj.GameObjectId,
						obj.EntityId,
						obj.BaseId,
						obj.Position,
						obj.IsTargetable,
						obj.ObjectKind.ToString(),
						obj.CurrentDistance);

					if (FsdChestInteraction.TryClassify(obj, out var chestKind))
					{
						bool isTreasureObject = obj.ObjectKind == global::Dalamud.Game.ClientState.Objects.Enums.ObjectKind.Treasure;
						bool isEventObject = obj.ObjectKind == global::Dalamud.Game.ClientState.Objects.Enums.ObjectKind.EventObj;
						var treasure = isTreasureObject ? (Treasure*)obj.Address : null;
						var completionKind = isTreasureObject
							? NativeTreasureCompletionKind.TreasureState
							: isEventObject
								? NativeTreasureCompletionKind.EventObjectTargetable
								: NativeTreasureCompletionKind.Unavailable;
						chests.Add(new FloorChestEvidence(
							evidence,
							chestKind,
							completionKind != NativeTreasureCompletionKind.Unavailable,
							completionKind,
							treasure != null ? treasure->State : Treasure.TreasureState.Unopened,
							treasure != null ? treasure->Flags : Treasure.TreasureFlags.None));
					}
					if (obj.BaseId == BandedChestLocator.HoardIndicatorBaseId)
					{
						hoardIndicators.Add(new FloorHoardIndicatorEvidence(
							evidence,
							obj.Name.ToString(),
							obj.ObjectKind.ToString(),
							obj.SubKind,
							$"0x{obj.Address.ToInt64():X}"));
					}
					if (RoomSearchContext.IsSightTrapIndicatorBaseId(obj.BaseId))
						sightTrapIndicators.Add(evidence);
					if (PassageLocator.IsPassageBase(obj.BaseId))
						passageActors.Add(evidence);
				}
				FullScanCount = FloorObjectEvidenceRefreshPlanner.NextCompletedScanCount(FullScanCount, scanCompleted: true);

				bool changed = Current == null ||
				               !Current.Available ||
				               !ChestSequenceEqual(Current.Chests, chests) ||
				               !HoardIndicatorSequenceEqual(Current.HoardIndicators, hoardIndicators) ||
				               !ObjectSequenceEqual(Current.SightTrapIndicators, sightTrapIndicators) ||
				               !ObjectSequenceEqual(Current.PassageActors, passageActors);
				long version = FloorObjectEvidenceRefreshPlanner.NextMaterialVersion(Current?.Version ?? 0, Current != null, changed);
				Current = new FloorObjectEvidenceSnapshot(
					true,
					version,
					RefreshCount,
					DateTime.UtcNow,
					playerPosition,
					scannedObjectCount,
					chests.ToArray(),
					hoardIndicators.ToArray(),
					sightTrapIndicators.ToArray(),
					passageActors.ToArray());
				return new FloorObjectEvidenceRefreshResult(true, decision.WasInvalidated, changed, true, Current);
			}
			catch (Exception ex)
			{
				bool changed = Current == null || Current.Available;
				long version = FloorObjectEvidenceRefreshPlanner.NextMaterialVersion(Current?.Version ?? 0, Current != null, changed);
				Current = new FloorObjectEvidenceSnapshot(
					false,
					version,
					RefreshCount,
					DateTime.UtcNow,
					playerPosition,
					scannedObjectCount,
					Array.Empty<FloorChestEvidence>(),
					Array.Empty<FloorHoardIndicatorEvidence>(),
					Array.Empty<FloorObjectEvidence>(),
					Array.Empty<FloorObjectEvidence>());
				if (changed || _lastErrorAtMs == 0 || nowMs - _lastErrorAtMs >= 2000)
				{
					_lastErrorAtMs = nowMs;
					Service.Log.Error($"[FloorObjectEvidence] Refresh failed: {ex}");
				}
				return new FloorObjectEvidenceRefreshResult(true, decision.WasInvalidated, changed, false, Current);
			}
		}

		public void Dispose()
		{
			IsDisposed = true;
			Current = null;
		}

		private static bool ObjectSequenceEqual(
			IReadOnlyList<FloorObjectEvidence> left,
			IReadOnlyList<FloorObjectEvidence> right)
		{
			if (left.Count != right.Count)
				return false;
			for (int i = 0; i < left.Count; i++)
			{
				if (!MaterialEquals(left[i], right[i]))
					return false;
			}
			return true;
		}

		private static bool ChestSequenceEqual(
			IReadOnlyList<FloorChestEvidence> left,
			IReadOnlyList<FloorChestEvidence> right)
		{
			if (left.Count != right.Count)
				return false;
			for (int i = 0; i < left.Count; i++)
			{
				if (!MaterialEquals(left[i].Object, right[i].Object) ||
				    left[i].Kind != right[i].Kind ||
				    left[i].NativeStateAvailable != right[i].NativeStateAvailable ||
				    left[i].NativeCompletionKind != right[i].NativeCompletionKind ||
				    left[i].State != right[i].State ||
				    left[i].Flags != right[i].Flags)
				{
					return false;
				}
			}
			return true;
		}

		private static bool HoardIndicatorSequenceEqual(
			IReadOnlyList<FloorHoardIndicatorEvidence> left,
			IReadOnlyList<FloorHoardIndicatorEvidence> right)
		{
			if (left.Count != right.Count)
				return false;
			for (int i = 0; i < left.Count; i++)
			{
				if (!MaterialEquals(left[i].Object, right[i].Object) ||
				    left[i].Name != right[i].Name ||
				    left[i].ObjectKind != right[i].ObjectKind ||
				    left[i].SubKind != right[i].SubKind ||
				    left[i].Address != right[i].Address)
				{
					return false;
				}
			}
			return true;
		}

		private static bool MaterialEquals(
			in FloorObjectEvidence left,
			in FloorObjectEvidence right) =>
			left.ObjectIndex == right.ObjectIndex &&
			left.GameObjectId == right.GameObjectId &&
			left.EntityId == right.EntityId &&
			left.BaseId == right.BaseId &&
			left.Position == right.Position &&
			left.IsTargetable == right.IsTargetable &&
			left.ObjectKind == right.ObjectKind;
	}
}
