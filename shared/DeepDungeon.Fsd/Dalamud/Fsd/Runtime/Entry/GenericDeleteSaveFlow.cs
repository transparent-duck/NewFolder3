using System;
using System.Collections.Generic;
using System.Numerics;
using global::Dalamud.Plugin.Services;
using DeepDungeon.Fsd.Dalamud.GameState;
using DeepDungeon.Fsd.Dalamud.Runtime;
using DeepDungeon.Fsd.Dalamud.Runtime.Navigation;

namespace DeepDungeon.Fsd.Dalamud.Runtime.Entry
{
	/// <summary>
	/// Reactive flow to delete a deep dungeon save slot via NPC �?menu �?save list �?yes/no confirm �?close menu.
	/// Mirrors PTDeleteSaveFlow behavior.
	/// </summary>
	public sealed class GenericDeleteSaveFlow
	{
		private RunContext? _ctx;
		private readonly DungeonData _dungeon;
		private readonly int _slotIndex; // 0 for slot 1, 1 for slot 2

		private readonly Dictionary<string, DateTime> _cooldowns = new Dictionary<string, DateTime>();
		private readonly Dictionary<string, DateTime> _seenSince = new Dictionary<string, DateTime>();
		private DateTime _nextTry = DateTime.MinValue;
		private string _lastStatus = string.Empty;
		private DateTime _lastLog = DateTime.MinValue;
		private DateTime _lastProgress = DateTime.MinValue;
		private DateTime _postInteractUntil = DateTime.MinValue;
		private DateTime _noWindowSince = DateTime.MinValue;
		private DateTime _lastMenuFire = DateTime.MinValue;
		private bool _firstProbeDone;
		private bool _deleteConfirmed;
		private bool _targetSlotObservedOccupied;
		private bool _deleteSlotClickSent;
		private bool _targetSlotObservedEmptyAfterDelete;
		private DateTime _lastNpcNotFound = DateTime.MinValue;
		private NavigationHelper? _npcNavHelper;

		private static readonly TimeSpan StabilityShort = TimeSpan.FromMilliseconds(150);
		private static readonly TimeSpan StabilityNormal = TimeSpan.FromMilliseconds(250);
		private static readonly TimeSpan CooldownNormal = TimeSpan.FromMilliseconds(400);
		private static readonly TimeSpan WatchdogTimeout = TimeSpan.FromSeconds(6);
		private static readonly TimeSpan PostInteractGrace = TimeSpan.FromMilliseconds(1500);
		private static readonly TimeSpan NoWindowReinteractDelay = TimeSpan.FromMilliseconds(2200);

		public GenericDeleteSaveFlow(DungeonData dungeon, int slotIndex)
		{
			_dungeon = dungeon;
			_slotIndex = slotIndex <= 0 ? 0 : 1;
		}

		public void Prepare(RunContext context)
		{
			_ctx = context;
			_cooldowns.Clear();
			_seenSince.Clear();
			_lastProgress = DateTime.Now;
			_nextTry = DateTime.MinValue;
			_postInteractUntil = DateTime.MinValue;
			_noWindowSince = DateTime.Now; // probe quickly after exit
			_lastMenuFire = DateTime.MinValue;
			_firstProbeDone = false;
			_deleteConfirmed = false;
			_targetSlotObservedOccupied = false;
			_deleteSlotClickSent = false;
			_targetSlotObservedEmptyAfterDelete = false;
			_npcNavHelper = new NavigationHelper(context.Navigator);
			if (_ctx != null)
			{
				_ctx.StatusLine = $"{_dungeon.Name} Del: prepared";
				try { Service.Log.Info($"[GenericDeleteSaveFlow] {_dungeon.Name} Del: prepared"); } catch { }
			}
		}

		public unsafe bool Update(IFramework framework)
		{
			if (_ctx == null) return false;
			if (DateTime.Now < _nextTry) return false;

			// Snapshot addons
			var hasYesno = DeepDungeonUi.TryGetSelectYesNo(out var yesno);
			var hasSave = DeepDungeonUi.TryGetAddon("DeepDungeonSaveData", out var save);
			var hasMenu = DeepDungeonUi.TryGetAddon("DeepDungeonMenu", out var menu);

			MarkPresence("yesno", hasYesno);
			MarkPresence("save", hasSave);
			MarkPresence("menu", hasMenu);
			var hasAnyUi = hasYesno || hasSave || hasMenu;
			UpdateNoWindow(hasAnyUi);
			if (hasAnyUi)
				_npcNavHelper?.Cancel();

			// 1) If SelectYesno present (delete confirm), confirm 0
			if (hasYesno && SeenStable("yesno", StabilityShort) && !IsOnCooldown("yesno"))
			{
				if (!_deleteSlotClickSent || !_targetSlotObservedOccupied)
				{
					return Fail($"{_dungeon.Name} Del: unexpected confirm before an occupied slot was selected");
				}
				if (!DeepDungeonUi.IsDeleteSaveConfirmationPrompt(yesno, out var promptError))
				{
					return Fail($"{_dungeon.Name} Del: {promptError}");
				}

				SetStatus($"{_dungeon.Name} Del: confirming delete");
				if (!DeepDungeonUi.Fire(yesno, 0))
				{
					return Fail($"{_dungeon.Name} Del: delete confirmation callback failed");
				}
				_deleteConfirmed = true;
				SetCooldown("yesno", CooldownNormal);
				_lastProgress = DateTime.Now;
				_nextTry = DateTime.Now.AddMilliseconds(300);
				return false;
			}

			// 2) If SaveData visible, click chosen slot via Agent
			if (hasSave && SeenStable("save", StabilityNormal) && !IsOnCooldown("save-slot"))
			{
				if (!DeepDungeonUi.TryGetEmptySlotsFromDeepDungeonSaveData(out var slot1Empty, out var slot2Empty))
				{
					SetStatus($"{_dungeon.Name} Del: waiting for save slot state");
					_nextTry = DateTime.Now.AddMilliseconds(250);
					return false;
				}

				bool targetSlotEmpty = _slotIndex == 0 ? slot1Empty : slot2Empty;
				if (_deleteConfirmed)
				{
					if (targetSlotEmpty)
					{
						_targetSlotObservedEmptyAfterDelete = true;
						SetStatus($"{_dungeon.Name} Del: slot {(_slotIndex == 0 ? 1 : 2)} verified empty");
						DeepDungeonUi.TryCloseAddon("DeepDungeonSaveData");
						_lastProgress = DateTime.Now;
					}
					else
					{
						SetStatus($"{_dungeon.Name} Del: waiting for slot {(_slotIndex == 0 ? 1 : 2)} to clear");
					}
					_nextTry = DateTime.Now.AddMilliseconds(250);
					return false;
				}

				if (targetSlotEmpty)
				{
					return Fail($"{_dungeon.Name} Del: slot {(_slotIndex == 0 ? 1 : 2)} is already empty");
				}

				SetStatus($"{_dungeon.Name} Del: clicking slot {(_slotIndex == 0 ? 1 : 2)}");
				// Use Agent with delete mode parameter
				if (!DeepDungeonUi.ClickSaveSlotForDelete(_slotIndex))
				{
					SetStatus($"{_dungeon.Name} Del: slot click failed");
					_nextTry = DateTime.Now.AddMilliseconds(250);
					return false;
				}
				_targetSlotObservedOccupied = true;
				_deleteSlotClickSent = true;
				SetCooldown("save-slot", CooldownNormal);
				_lastProgress = DateTime.Now;
				_nextTry = DateTime.Now.AddMilliseconds(300);
				return false;
			}

			// 3) If Menu visible, open SaveData for deletion via Agent (robust - not affected by menu order)
			// Note: We no longer close the menu after delete - GenericEntryFlow will reuse the open menu on next loop
			if (hasMenu && SeenStable("menu", StabilityShort))
			{
				// Open SaveData for deletion via Agent
				if ((!_deleteConfirmed || !_targetSlotObservedEmptyAfterDelete) && !IsOnCooldown("menu-open-save") && DateTime.Now - _lastMenuFire >= TimeSpan.FromMilliseconds(350))
				{
					SetStatus(_deleteConfirmed
						? $"{_dungeon.Name} Del: opening save data to verify slot"
						: $"{_dungeon.Name} Del: opening save data via Agent");
					DeepDungeonUi.OpenDeleteSaveViaAgent();
					_lastMenuFire = DateTime.Now;
					SetCooldown("menu-open-save", CooldownNormal);
					_lastProgress = DateTime.Now;
					_nextTry = DateTime.Now.AddMilliseconds(250);
					return false;
				}
			}

			// Finish after we confirmed a delete and save/yesno are closed (menu can remain open for next loop)
			if (_deleteConfirmed && !hasSave && !hasYesno && !_targetSlotObservedEmptyAfterDelete)
			{
				SetStatus($"{_dungeon.Name} Del: waiting to verify slot empty");
				_nextTry = DateTime.Now.AddMilliseconds(250);
				return false;
			}

			if (_deleteConfirmed && _targetSlotObservedEmptyAfterDelete && (hasSave || hasMenu || hasYesno))
			{
				SetStatus($"{_dungeon.Name} Del: closing entry windows");
				DeepDungeonUi.CloseDeepDungeonEntryWindows();
				_nextTry = DateTime.Now.AddMilliseconds(250);
				return false;
			}

			if (_deleteConfirmed && _targetSlotObservedEmptyAfterDelete && !hasSave && !hasMenu && !hasYesno && (DateTime.Now - _lastProgress) > TimeSpan.FromMilliseconds(400))
			{
				_npcNavHelper?.Cancel();
				SetStatus($"{_dungeon.Name} Del: finished");
				return true;
			}

			// 4) Fallback: interact with NPC if no UI visible, honoring grace and no-window delay
			if (!hasAnyUi && !IsOnCooldown("npc-probe"))
			{
				var inGrace = DateTime.Now < _postInteractUntil;
				var noWindowLongEnough = _noWindowSince != DateTime.MinValue && (DateTime.Now - _noWindowSince) >= NoWindowReinteractDelay;
				var allowFirstProbe = !_firstProbeDone;
				var preferNpcReprobe = _lastNpcNotFound != DateTime.MinValue;
				if (allowFirstProbe || preferNpcReprobe || (!inGrace && noWindowLongEnough))
				{
					SetStatus($"{_dungeon.Name} Del: interacting with NPC");
					InteractWithDungeonNpc();
					var probeCooldown = preferNpcReprobe ? TimeSpan.FromMilliseconds(600) : TimeSpan.FromMilliseconds(900);
					SetCooldown("npc-probe", probeCooldown);
					_firstProbeDone = true;
					_lastProgress = DateTime.Now;
					_nextTry = DateTime.Now.AddMilliseconds(350);
					return false;
				}
			}

			_nextTry = DateTime.Now.AddMilliseconds(150);
			return false;

			void InteractWithDungeonNpc()
			{
				if (_dungeon.NpcDataId == 0)
				{
					SetStatus($"{_dungeon.Name} Del: NPC id unset");
					return;
				}

				var npc = NpcInteractionGuard.FindByBaseId(_dungeon.NpcDataId);
				if (npc == null)
				{
					_lastNpcNotFound = DateTime.Now;
					SetStatus($"{_dungeon.Name} Del: NPC not found, waiting for load...");
					return;
				}

				var player = Service.LocalPlayer;
				if (player == null)
				{
					SetStatus($"{_dungeon.Name} Del: waiting for player");
					return;
				}

				var distance = Vector3.Distance(player.Position, npc.Position);
				if (distance > NpcInteractionGuard.MaxInteractDistance)
				{
					var navState = _npcNavHelper?.Navigate(npc.Position, player.Position, NpcInteractionGuard.MaxInteractDistance - 0.4f) ?? NavigationState.Failed;
					SetStatus(navState is NavigationState.Failed or NavigationState.StuckGiveUp
						? $"{_dungeon.Name} Del: NPC navigation failed ({distance:F1}m)"
						: $"{_dungeon.Name} Del: moving to NPC ({distance:F1}m)");
					return;
				}

				_npcNavHelper?.Cancel();
				if (!NpcInteractionGuard.TryInteract(_dungeon.NpcDataId, $"{_dungeon.Name} Del", out var status))
				{
					_lastNpcNotFound = DateTime.Now;
					SetStatus(status);
					return;
				}

				SetStatus(status);
				_postInteractUntil = DateTime.Now.Add(PostInteractGrace);
			}
			void MarkPresence(string key, bool present)
			{
				if (present)
				{
					if (!_seenSince.ContainsKey(key)) _seenSince[key] = DateTime.Now;
				}
				else
				{
					if (_seenSince.ContainsKey(key)) _seenSince.Remove(key);
				}
			}
			bool SeenStable(string key, TimeSpan minStable)
			{
				if (_seenSince.TryGetValue(key, out var t)) return (DateTime.Now - t) >= minStable;
				return false;
			}
			bool IsOnCooldown(string key)
			{
				if (_cooldowns.TryGetValue(key, out var until)) return DateTime.Now < until;
				return false;
			}
			void SetCooldown(string key, TimeSpan duration)
			{
				_cooldowns[key] = DateTime.Now.Add(duration);
			}
			void UpdateNoWindow(bool hasAnyUiNow)
			{
				if (hasAnyUiNow)
				{
					_noWindowSince = DateTime.MinValue;
				}
				else
				{
					if (_noWindowSince == DateTime.MinValue) _noWindowSince = DateTime.Now;
				}
			}
			void SetStatus(string s)
			{
				if (_ctx == null) return;
				_ctx.StatusLine = s;
				if (!string.Equals(_lastStatus, s, StringComparison.Ordinal) || (DateTime.Now - _lastLog).TotalSeconds >= 2)
				{
					_lastStatus = s;
					_lastLog = DateTime.Now;
					try { Service.Log.Info($"[GenericDeleteSaveFlow] {s}"); } catch { }
				}
			}

			bool Fail(string s)
			{
				_npcNavHelper?.Cancel();
				SetStatus(s);
				if (_ctx != null)
				{
					_ctx.StatusIsError = true;
				}
				return true;
			}
		}
	}
}
