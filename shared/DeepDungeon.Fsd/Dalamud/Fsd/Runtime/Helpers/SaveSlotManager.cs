namespace DeepDungeon.Fsd.Dalamud.Runtime.Helpers
{
	/// <summary>
	/// Selects an empty deep-dungeon save slot and tracks the selected slot.
	/// </summary>
	public sealed class SaveSlotManager
	{
		private int _lastUsedSlotIndex = -1;

		public bool TryFindEmptySlot(int preferredSlotIndex, out int chosenSlotIndex)
		{
			chosenSlotIndex = -1;
			try
			{
				if (GameState.DeepDungeonUi.TryGetEmptySlotsFromDeepDungeonSaveData(out var slot1Empty, out var slot2Empty))
				{
					var pref = preferredSlotIndex <= 0 ? 0 : 1;
					if (pref == 0 && slot1Empty) { chosenSlotIndex = 0; return true; }
					if (pref == 1 && slot2Empty) { chosenSlotIndex = 1; return true; }
					if (slot1Empty) { chosenSlotIndex = 0; return true; }
					if (slot2Empty) { chosenSlotIndex = 1; return true; }
				}
			}
			catch { }
			return false;
		}

		public bool TrySelectPreferredEmpty(int preferredSlotIndex, out int chosenSlotIndex)
		{
			if (TryFindEmptySlot(preferredSlotIndex, out chosenSlotIndex))
			{
				return TrySelectSlot(chosenSlotIndex);
			}
			return false;
		}

		public bool TrySelectSlot(int slotIndex)
		{
			try
			{
				var idx = slotIndex <= 0 ? 0 : 1;
				if (GameState.DeepDungeonUi.ClickSaveSlotForEntry(idx))
				{
					_lastUsedSlotIndex = idx;
					return true;
				}
			}
			catch { }
			return false;
		}

		public int LastUsedSlotIndex => _lastUsedSlotIndex;
	}
}

