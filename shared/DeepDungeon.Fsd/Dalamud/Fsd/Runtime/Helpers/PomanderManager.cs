using System;
using DeepDungeon.Fsd.Dalamud;
using FFXIVClientStructs.FFXIV.Client.Game.InstanceContent;

namespace DeepDungeon.Fsd.Dalamud.Runtime.Helpers
{
	/// <summary>
	/// Minimal helper to check and use pomanders via the deep dungeon director.
	/// </summary>
	public sealed class PomanderManager
	{
		private static readonly TimeSpan FailureLogInterval = TimeSpan.FromSeconds(5);
		private DateTime _nextFailureLogAtUtc = DateTime.MinValue;

		/// <summary>
		/// Returns whether the native request was dispatched. The native method is
		/// void, so this does not prove that the server accepted or consumed it.
		/// </summary>
		public unsafe bool Use(uint pomanderSlotIndex)
		{
			if (!TryGetDeepDungeon(nameof(Use), out var dd))
				return false;

			dd->UsePomander(pomanderSlotIndex);
			return true;
		}

		public unsafe bool IsUsable(uint pomanderSlotIndex)
		{
			if (!TryGetDeepDungeon(nameof(IsUsable), out var dd))
				return false;

			var items = dd->Items;
			if (pomanderSlotIndex >= items.Length) return false;
			return items[(int)pomanderSlotIndex].IsUsable && items[(int)pomanderSlotIndex].Count > 0;
		}

		public unsafe bool IsActive(uint pomanderSlotIndex)
		{
			return TryIsActive(pomanderSlotIndex, out bool isActive) && isActive;
		}

		public unsafe bool TryIsActive(uint pomanderSlotIndex, out bool isActive)
		{
			isActive = false;
			if (!TryGetDeepDungeon(nameof(TryIsActive), out var dd))
				return false;

			var items = dd->Items;
			if (pomanderSlotIndex >= items.Length)
			{
				LogFailure(nameof(TryIsActive), $"slot {pomanderSlotIndex} is outside the native item array");
				return false;
			}

			isActive = items[(int)pomanderSlotIndex].IsActive;
			return true;
		}

		public unsafe int GetCount(uint pomanderSlotIndex)
		{
			if (!TryGetDeepDungeon(nameof(GetCount), out var dd))
				return 0;

			var items = dd->Items;
			if (pomanderSlotIndex >= items.Length) return 0;
			return items[(int)pomanderSlotIndex].Count;
		}

		public unsafe int GetStoneCount(byte stoneId)
		{
			if (!TryGetDeepDungeon(nameof(GetStoneCount), out var dd))
				return 0;

			int count = 0;
			var stones = dd->Magicite;
			for (int i = 0; i < stones.Length; i++)
			{
				if (stones[i] == stoneId)
					count++;
			}
			return count;
		}

		/// <summary>
		/// Returns whether a matching slot was found and its native request was
		/// dispatched. This does not prove that the server accepted or consumed it.
		/// </summary>
		public unsafe bool UseStone(byte stoneId)
		{
			if (!TryGetDeepDungeon(nameof(UseStone), out var dd))
				return false;

			var stones = dd->Magicite;
			for (int i = 0; i < stones.Length; i++)
			{
				if (stones[i] != stoneId)
					continue;
				dd->UseStone((uint)i);
				return true;
			}
			return false;
		}

		private unsafe bool TryGetDeepDungeon(string operation, out InstanceContentDeepDungeon* dd)
		{
			dd = null;

			try
			{
				var efw = FFXIVClientStructs.FFXIV.Client.Game.Event.EventFramework.Instance();
				dd = efw != null ? efw->GetInstanceContentDeepDungeon() : null;
				if (dd != null)
					return true;

				LogFailure(operation, "native deep-dungeon state is unavailable");
				return false;
			}
			catch (Exception ex)
			{
				LogFailure(operation, ex);
				return false;
			}
		}

		private void LogFailure(string operation, Exception ex)
		{
			LogFailure(operation, ex.ToString());
		}

		private void LogFailure(string operation, string reason)
		{
			var now = DateTime.UtcNow;
			if (now < _nextFailureLogAtUtc)
				return;

			_nextFailureLogAtUtc = now.Add(FailureLogInterval);
			Service.Log.Error($"[PomanderManager] {operation} failed: {reason}");
		}
	}
}
