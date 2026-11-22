using System;
using DeepDungeon.Fsd.Runtime;
using global::Dalamud.Game.ClientState.Conditions;
using global::Dalamud.Game.ClientState.Objects.Types;
using global::Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using FFXIVClientStructs.FFXIV.Client.Game.InstanceContent;

namespace DeepDungeon.Fsd.Dalamud.GameState
{
	/// <summary>
	/// Single source of truth for deep dungeon duty state.
	/// Merges the old DeepDungeonState (floor type and stabilization)
	/// and DutyStateWatcher (duty detection, passage, transition).
	/// Owned by RunHost; referenced by RunContext.
	/// </summary>
	public sealed class DutyState
	{
		// Duty detection
		public bool IsInDuty { get; private set; }
		public uint DungeonId { get; private set; }
		public byte Floor { get; private set; }
		public int HoardCount { get; private set; }
		public bool PassageOpen { get; private set; }
		public bool IsBossFloor { get; private set; }
		public bool IsTransporting { get; private set; }
		public bool IsTransitioning { get; private set; }
		public bool StateReadFailed => LastStateReadError.Length != 0;
		internal bool StateReadIncompatible { get; private set; }
		public string LastStateReadError { get; private set; } = string.Empty;

		// Floor type (enum form of IsBossFloor, with stabilization)
		public DeepDungeonFloorKind CurrentFloorKind { get; private set; } = DeepDungeonFloorKind.Unknown;

		// Stabilization tracking
		private byte _lastKnownFloor;
		private uint _lastKnownDungeonId;
		private DateTime _lastFloorChangeAt = DateTime.MinValue;
		private DateTime _lastStateReadFailureLogAt = DateTime.MinValue;
		private bool _floorTypeClassified;
		private const int FloorChangeStabilizationMs = 2000;

		public unsafe bool Update(IFramework _)
		{
			try
			{
				var player = Service.LocalPlayer;
				var efw = EventFramework.Instance();
				var dd = efw != null ? efw->GetInstanceContentDeepDungeon() : null;
				var isTransporting = IsInTransportState(player, dd);

				if (dd == null)
				{
					MarkStateReadFailure("暫未取得深宮副本控制器。");
					return false;
				}

				uint dungeonId = dd->DeepDungeonId;
				byte floor = dd->Floor;
				if (!isTransporting && dungeonId == 0)
				{
					MarkStateReadFailure("深宮副本控制器尚未提供地宮編號。");
					return false;
				}
				if (!isTransporting &&
				    (DeepDungeonFloorClassifier.Classify(dungeonId, floor) == DeepDungeonFloorKind.Unknown ||
				     DungeonCatalog.TryGetByTerritoryId(Service.ClientState.TerritoryType, out var dungeon) &&
				     dungeon.DutyDungeonId != dungeonId))
				{
					MarkStateReadFailure($"副本控制器資料與目前區域不相容（區域 {Service.ClientState.TerritoryType}、地宮 {dungeonId}、樓層 {floor}）。",
						incompatible: true);
					return false;
				}
				int hoardCount = (int)dd->HoardCount;
				bool passageOpen = DeepDungeonHelper.IsPassageOpen(dd);
				bool isBossFloor = DeepDungeonHelper.IsBossFloor(dungeonId, floor);

				IsInDuty = true;
				DungeonId = dungeonId;
				Floor = floor;
				HoardCount = hoardCount;
				PassageOpen = passageOpen;
				IsBossFloor = isBossFloor;
				IsTransporting = isTransporting;
				IsTransitioning = isTransporting;

				if (Floor > 0)
				{
					DeepDungeonFloorsetTracker.Update(dd, IsTransitioning);
				}

				if (Floor > 0)
				{
					UpdateFloorType(dungeonId, floor, isBossFloor);
				}
				else
				{
					CurrentFloorKind = DeepDungeonFloorKind.Unknown;
				}

				ClearStateReadFailure();
				return true;
			}
			catch (Exception ex)
			{
				MarkStateReadFailure(ex.Message, ex is MissingMethodException or TypeLoadException or EntryPointNotFoundException);
				if ((DateTime.UtcNow - _lastStateReadFailureLogAt).TotalSeconds >= 2)
				{
					_lastStateReadFailureLogAt = DateTime.UtcNow;
					Service.Log.Error($"[DutyState] Deep dungeon state read failed: {ex}");
				}
				return false;
			}
		}

		private void ClearStateReadFailure()
		{
			StateReadIncompatible = false;
			LastStateReadError = string.Empty;
		}

		private void MarkStateReadFailure(string reason, bool incompatible = false)
		{
			StateReadIncompatible = incompatible;
			LastStateReadError = reason;
			ResetDutyFields(isTransporting: true, isTransitioning: true);
		}

		private void UpdateFloorType(uint dungeonId, byte floor, bool isBoss)
		{
			if (floor != _lastKnownFloor || dungeonId != _lastKnownDungeonId)
			{
				_lastKnownFloor = floor;
				_lastKnownDungeonId = dungeonId;
				_lastFloorChangeAt = DateTime.UtcNow;
				_floorTypeClassified = false;
				return;
			}
			if (_floorTypeClassified)
				return;

			if (_lastFloorChangeAt != DateTime.MinValue &&
				(DateTime.UtcNow - _lastFloorChangeAt).TotalMilliseconds < 350)
				return;

			if (DeepDungeonHelper.TryGetFloorRangeForCurrentTerritory(out var fs, out var fe))
			{
				if (floor < fs || floor > fe)
					return;
			}

			CurrentFloorKind = DeepDungeonFloorClassifier.Classify(dungeonId, floor);
			_floorTypeClassified = true;
		}

		private unsafe bool IsInTransportState(ICharacter? player, InstanceContentDeepDungeon* dd)
		{
			if (player == null)
				return true;

			if (Service.Condition[ConditionFlag.BetweenAreas] ||
				Service.Condition[ConditionFlag.BetweenAreas51])
				return true;

			if (dd == null)
				return true;

			if (dd->Floor == 0)
				return true;

			return false;
		}

		public bool IsPlayerPositionStable()
		{
			if (_lastFloorChangeAt == DateTime.MinValue)
				return true;
			return (DateTime.UtcNow - _lastFloorChangeAt).TotalMilliseconds >= FloorChangeStabilizationMs;
		}

		public void MarkOutsideDuty()
		{
			bool transitioning = IsClientTransitioning;
			ResetDutyFields(isTransporting: transitioning, isTransitioning: transitioning);
			_lastKnownFloor = 0;
			_lastKnownDungeonId = 0;
			_lastFloorChangeAt = DateTime.MinValue;
			ClearStateReadFailure();
		}

		// Read live conditions: the duty object can disappear before zoning finishes.
		public static bool IsClientTransitioning => Service.LocalPlayer == null ||
			Service.Condition[ConditionFlag.BetweenAreas] ||
			Service.Condition[ConditionFlag.BetweenAreas51];

		public double GetStabilizationTimeRemaining()
		{
			if (_lastFloorChangeAt == DateTime.MinValue)
				return 0.0;
			double elapsed = (DateTime.UtcNow - _lastFloorChangeAt).TotalMilliseconds;
			double remaining = FloorChangeStabilizationMs - elapsed;
			return Math.Max(0.0, remaining / 1000.0);
		}

		private void ResetDutyFields(bool isTransporting, bool isTransitioning)
		{
			IsInDuty = false;
			DungeonId = 0;
			Floor = 0;
			HoardCount = 0;
			PassageOpen = false;
			IsBossFloor = false;
			IsTransporting = isTransporting;
			IsTransitioning = isTransitioning;
			CurrentFloorKind = DeepDungeonFloorKind.Unknown;
			_floorTypeClassified = false;
		}
	}
}
