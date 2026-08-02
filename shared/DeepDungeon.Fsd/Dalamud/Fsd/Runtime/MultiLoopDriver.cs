using System;
using System.Collections.Generic;
using DeepDungeon.Fsd.Dalamud.GameState;

namespace DeepDungeon.Fsd.Dalamud.Runtime
{
	/// <summary>
	/// Owns multi-loop state and end-mode logic for FSD.
	/// Extracted from RunHost to keep the host focused on
	/// mode switching and controller coordination.
	/// </summary>
	public sealed class MultiLoopDriver
	{
		private readonly FsdSettings _configuration;

		public int TargetLoops { get; private set; } = 1;
		public int CompletedLoops { get; private set; }
		public bool InfiniteLoop { get; private set; }
		public string LastStopReason { get; private set; } = string.Empty;
		public bool LastStopWasError { get; private set; }

		private FsdEndMode _endMode = FsdEndMode.Loops;
		private uint _currentDungeonId;
		private uint _potsherdItemId;
		private int _potsherdTarget;
		private readonly Dictionary<uint, int> _hoardTargets = new();

		public MultiLoopDriver(FsdSettings configuration, int targetLoops, bool infinite)
		{
			_configuration = configuration;
			TargetLoops = Math.Max(1, targetLoops);
			InfiniteLoop = infinite;
			ConfigureEndMode();
		}

		public void IncrementLoop()
		{
			CompletedLoops++;
		}

		public void ObserveDutyState(DutyState duty)
		{
			if (_currentDungeonId != 0 || duty.DungeonId == 0)
				return;

			_currentDungeonId = duty.DungeonId;
			ConfigureDungeonSpecificTargets();
		}

		/// <summary>
		/// Returns true if the engine should stop after the current run completes.
		/// </summary>
		public bool ShouldStopAfterCurrentRun(DutyState duty)
		{
			ObserveDutyState(duty);

			switch (_endMode)
			{
				case FsdEndMode.Loops:
					if (InfiniteLoop) return ContinueAfterCurrentRun();
					if (CompletedLoops + 1 >= TargetLoops)
						return StopAfterCurrentRun($"Loop target reached ({CompletedLoops + 1}/{TargetLoops})");
					return ContinueAfterCurrentRun();

				case FsdEndMode.Potsherd:
					if (_potsherdItemId == 0 || _potsherdTarget <= 0)
						return StopAfterCurrentRun("Potsherd end mode is missing a dungeon item or positive target.", isError: true);
					if (!DeepDungeonLootTracker.TryGetItemCount(_potsherdItemId, out int current, out string potsherdError))
						return StopAfterCurrentRun($"Cannot read potsherd count for item {_potsherdItemId}: {potsherdError}", isError: true);
					Service.Log.Info($"[MultiLoop] Potsherd check: item={_potsherdItemId} current={current} target={_potsherdTarget}");
					if (current >= _potsherdTarget)
						return StopAfterCurrentRun($"Potsherd target reached ({current}/{_potsherdTarget})");
					return ContinueAfterCurrentRun();

				case FsdEndMode.Hoard:
					if (_hoardTargets.Count == 0)
						return StopAfterCurrentRun("Hoard end mode has no configured item targets for this dungeon.", isError: true);
					bool hasPositiveTarget = false;
					foreach (var kv in _hoardTargets)
					{
						if (kv.Value <= 0) continue;
						hasPositiveTarget = true;
						if (!DeepDungeonLootTracker.TryGetItemCount(kv.Key, out int cnt, out string hoardError))
							return StopAfterCurrentRun($"Cannot read hoard count for item {kv.Key}: {hoardError}", isError: true);
						Service.Log.Info($"[MultiLoop] Hoard check: item={kv.Key} current={cnt} target={kv.Value}");
						if (cnt < kv.Value)
							return ContinueAfterCurrentRun();
					}
					if (!hasPositiveTarget)
						return StopAfterCurrentRun("Hoard end mode has no positive item target.", isError: true);
					return StopAfterCurrentRun("All hoard targets reached.");

				default:
					return ContinueAfterCurrentRun();
			}
		}

		private bool ContinueAfterCurrentRun()
		{
			LastStopReason = string.Empty;
			LastStopWasError = false;
			return false;
		}

		private bool StopAfterCurrentRun(string reason, bool isError = false)
		{
			LastStopReason = reason;
			LastStopWasError = isError;
			if (isError)
				Service.Log.Warning($"[MultiLoop] Stopping FSD: {reason}");
			else
				Service.Log.Info($"[MultiLoop] Stopping FSD: {reason}");
			return true;
		}

		private void ConfigureEndMode()
		{
			try
			{
				// TODO: Deprecated item-count end modes; FSD should only stop by loop count.
				_endMode = FsdEndMode.Loops;
				if (_configuration.NecromancerFsdEndMode != (int)FsdEndMode.Loops)
				{
					_configuration.NecromancerFsdEndMode = (int)FsdEndMode.Loops;
					_configuration.Save();
				}

				_currentDungeonId = 0;
				_potsherdItemId = 0;
				_potsherdTarget = 0;
				_hoardTargets.Clear();
			}
			catch
			{
				_endMode = FsdEndMode.Loops;
			}
		}

		private void ConfigureDungeonSpecificTargets()
		{
			try
			{
				if (!DungeonCatalog.TryGetByDungeonId(_currentDungeonId, out var dungeon))
					return;

				if (_endMode == FsdEndMode.Potsherd)
				{
					_potsherdItemId = dungeon.PotsherdItemId;
					_potsherdTarget = Math.Max(0, GetConfigPotsherdTarget(dungeon.DungeonId));
				}
				else if (_endMode == FsdEndMode.Hoard)
				{
					_hoardTargets.Clear();
					foreach (var itemId in dungeon.HoardItemIds)
						_hoardTargets[itemId] = Math.Max(0, GetConfigHoardTarget(dungeon.DungeonId, itemId));
				}
			}
			catch
			{
				_potsherdItemId = 0;
				_potsherdTarget = 0;
				_hoardTargets.Clear();
			}
		}

		private int GetConfigPotsherdTarget(uint dungeonId) => dungeonId switch
		{
			1 => _configuration.NecromancerFsdPotdPotsherdTarget,
			2 => _configuration.NecromancerFsdHoHPotsherdTarget,
			3 => _configuration.NecromancerFsdEOPotsherdTarget,
			4 => _configuration.NecromancerFsdPTPotsherdTarget,
			_ => 0
		};

		private int GetConfigHoardTarget(uint dungeonId, uint itemId)
		{
			return (dungeonId, itemId) switch
			{
				(1, 16170) => _configuration.NecromancerFsdPotdHoard16170Target,
				(1, 16171) => _configuration.NecromancerFsdPotdHoard16171Target,
				(1, 16172) => _configuration.NecromancerFsdPotdHoard16172Target,
				(1, 16173) => _configuration.NecromancerFsdPotdHoard16173Target,
				(2, 23223) => _configuration.NecromancerFsdHoHHoard23223Target,
				(2, 23224) => _configuration.NecromancerFsdHoHHoard23224Target,
				(2, 23225) => _configuration.NecromancerFsdHoHHoard23225Target,
				(3, 38945) => _configuration.NecromancerFsdEOHoard38945Target,
				(3, 38946) => _configuration.NecromancerFsdEOHoard38946Target,
				(3, 38947) => _configuration.NecromancerFsdEOHoard38947Target,
				(4, 47104) => _configuration.NecromancerFsdPTHoard47104Target,
				(4, 47105) => _configuration.NecromancerFsdPTHoard47105Target,
				(4, 47106) => _configuration.NecromancerFsdPTHoard47106Target,
				_ => 0
			};
		}
	}
}
