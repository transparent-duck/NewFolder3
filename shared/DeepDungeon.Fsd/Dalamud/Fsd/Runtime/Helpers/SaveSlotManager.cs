namespace DeepDungeon.Fsd.Dalamud.Runtime.Helpers
{
	/// <summary>
	/// Selects an empty deep-dungeon save slot and tracks the selected slot.
	/// </summary>
	public sealed class SaveSlotManager
	{
		private int _lastUsedSlotIndex = -1;

		public bool TryFindEmptySlot(int preferredSlotIndex, out int chosenSlotIndex)
			=> TryFindEmptySlot(preferredSlotIndex, out chosenSlotIndex, out _);

        private bool TryFindEmptySlot(int preferredSlotIndex, out int chosenSlotIndex, out string error)
		{
			chosenSlotIndex = -1;
            if (!GameState.DeepDungeonUi.TryReadSaveSlots(out var slots, out error)) return false;
            foreach (var slot in slots)
                if (slot.Index == preferredSlotIndex && slot.Empty && slot.Enterable) { chosenSlotIndex = slot.Index; return true; }
            foreach (var slot in slots)
                if (slot.Empty && slot.Enterable) { chosenSlotIndex = slot.Index; return true; }
            error = "沒有可用的空存檔槽位。";
            return false;
        }

		public bool TrySelectPreferredEmpty(int preferredSlotIndex, out int chosenSlotIndex)
			=> TrySelectPreferredEmpty(preferredSlotIndex, out chosenSlotIndex, out _);

        public bool TrySelectPreferredEmpty(int preferredSlotIndex, out int chosenSlotIndex, out string error)
		{
			if (TryFindEmptySlot(preferredSlotIndex, out chosenSlotIndex, out error))
			{
				if (TrySelectSlot(chosenSlotIndex)) return true;
                error = "空存檔槽位選取失敗；列表可能已變更。";
			}
			return false;
		}

		public bool TrySelectSlot(int slotIndex)
		{
			try
			{
				if (slotIndex < 0) return false;
                var idx = slotIndex;
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

