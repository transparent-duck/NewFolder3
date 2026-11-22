using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using DeepDungeon.Fsd.Core;
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
		private EntranceWindowCleanup _cleanup = new();
		private RunContext? _ctx;
		private readonly DungeonData _dungeon;
		private readonly Func<IReadOnlyList<DeepDungeon.Fsd.Core.SaveSlotSnapshot>, string?>? _validateSource;
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
		private bool _deleteSlotClickSent;
		private long? _deleteSubmittedAt;
		private bool _targetSlotObservedEmptyAfterDelete;
		private bool _freshListRequested;
		private DateTime _lastNpcNotFound = DateTime.MinValue;
		private NavigationHelper? _npcNavHelper;

		private static readonly TimeSpan StabilityShort = TimeSpan.FromMilliseconds(150);
		private static readonly TimeSpan StabilityNormal = TimeSpan.FromMilliseconds(250);
		private static readonly TimeSpan DeleteVerificationWindow = TimeSpan.FromSeconds(10);

		public GenericDeleteSaveFlow(DungeonData dungeon, int slotIndex,
			Func<IReadOnlyList<DeepDungeon.Fsd.Core.SaveSlotSnapshot>, string?>? validateSource = null)
		{
			_dungeon = dungeon;
			_validateSource = validateSource;
			_slotIndex = slotIndex >= 0 ? slotIndex : throw new ArgumentOutOfRangeException(nameof(slotIndex));
		}

		public void Prepare(RunContext context)
		{
			_ctx = context;
			_cleanup = new();
			_cooldowns.Clear();
			_seenSince.Clear();
			_lastProgress = DateTime.Now;
			_nextTry = DateTime.MinValue;
			_postInteractUntil = DateTime.MinValue;
			_noWindowSince = DateTime.Now; // probe quickly after exit
			_lastMenuFire = DateTime.MinValue;
			_firstProbeDone = false;
			_deleteSlotClickSent = false;
			_deleteSubmittedAt = null;
			_targetSlotObservedEmptyAfterDelete = false;
			_freshListRequested = false;
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
			if (DutyState.IsClientTransitioning || _ctx.Duty.IsInDuty) return false;
			if (DateTime.Now < _nextTry) return false;

			if (!_cleanup.Ready(_ctx)) return false;

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

			// 1) A modal blocks all lower-priority actions, including while stabilizing.
			if (hasYesno)
			{
				if (_deleteSubmittedAt.HasValue)
				{
					if (!_targetSlotObservedEmptyAfterDelete && DeleteVerificationExpired("yesno"))
						return Fail($"已送出存檔 {_slotIndex + 1} 的刪除確認，但對話仍阻止重新讀取該槽。刪除結果未確認。");
					SetStatus($"{_dungeon.Name} Del: waiting for the same slot's delete result");
					return false;
				}
				if (!SeenStable("yesno", StabilityShort) || IsOnCooldown("yesno")) return false;
				if (!_deleteSlotClickSent)
				{
					SetStatus($"{_dungeon.Name} Del: waiting for confirmation with a known target slot");
					return false;
				}
				if (!DeepDungeonUi.IsDeleteSaveConfirmationPrompt(yesno, out var promptError))
				{
					SetStatus($"{_dungeon.Name} Del: waiting for unrelated confirmation ({promptError})");
					return false;
				}

				SetStatus($"{_dungeon.Name} Del: confirming delete");
				if (!DeepDungeonUi.Fire(yesno, 0))
				{
					return Fail($"{_dungeon.Name} Del: delete confirmation callback failed");
				}
				_deleteSubmittedAt = Environment.TickCount64;
				_freshListRequested = false;
				SetCooldown("yesno", InteractionTiming.GetRetryInterval());
				_lastProgress = DateTime.Now;
				_nextTry = DateTime.Now.Add(InteractionTiming.GetRetryInterval());
				return false;
			}

			// 2) If SaveData visible, click chosen slot via Agent
			if (hasSave && SeenStable("save", StabilityNormal) && !IsOnCooldown("save-slot"))
			{
				// Entry panels can survive zoning with their pre-entry empty rows.
				// Require a new reset-list request before using any row as a result.
				if (!_freshListRequested)
				{
					SetStatus($"{_dungeon.Name} Del: closing residual save list before refresh");
					DeepDungeonUi.TryCloseAddon("DeepDungeonSaveData");
					_nextTry = DateTime.Now.Add(InteractionTiming.GetRetryInterval());
					return false;
				}
				if (!DeepDungeonUi.IsSaveListForDeletion(save))
				{
					SetStatus($"{_dungeon.Name} Del: closing a non-delete save list");
					DeepDungeonUi.TryCloseAddon("DeepDungeonSaveData");
					_nextTry = DateTime.Now.Add(InteractionTiming.GetRetryInterval());
					return false;
				}
				if (!DeepDungeonUi.TryReadSaveSlots(out var slots, out _) || !slots.Any(slot => slot.Index == _slotIndex))
				{
					if (!_targetSlotObservedEmptyAfterDelete && DeleteVerificationExpired("save"))
						return Fail($"已送出刪除確認，但無法從重新開啟的列表讀取存檔 {_slotIndex + 1}。刪除結果未確認。");
					SetStatus($"{_dungeon.Name} Del: waiting for save slot state");
					_nextTry = DateTime.Now.AddMilliseconds(250);
					return false;
				}

				bool targetSlotEmpty = slots.Single(slot => slot.Index == _slotIndex).Empty;
				// Completion is a game fact. A user or YesAlready may have confirmed.
				if (targetSlotEmpty)
				{
					_targetSlotObservedEmptyAfterDelete = true;
					SetStatus($"{_dungeon.Name} Del: slot {(_slotIndex + 1)} verified empty");
					DeepDungeonUi.TryCloseAddon("DeepDungeonSaveData");
					_lastProgress = DateTime.Now;
					_nextTry = DateTime.Now.Add(InteractionTiming.GetRetryInterval());
					return false;
				}
				if (_deleteSubmittedAt.HasValue)
					return Fail($"已送出刪除確認，但重新讀取後存檔 {_slotIndex + 1} 仍非空，無法確認刪除結果。");
				string? sourceError = _validateSource?.Invoke(slots);
				if (!string.IsNullOrEmpty(sourceError)) return Fail(sourceError);
				SetStatus($"{_dungeon.Name} Del: clicking slot {(_slotIndex + 1)}");
				// Use Agent with delete mode parameter
				if (!DeepDungeonUi.ClickSaveSlotForDelete(_slotIndex))
				{
					SetStatus($"{_dungeon.Name} Del: slot click failed");
					_nextTry = DateTime.Now.Add(InteractionTiming.GetRetryInterval());
					return false;
				}
				_deleteSlotClickSent = true;
				SetCooldown("save-slot", InteractionTiming.GetRetryInterval());
				_lastProgress = DateTime.Now;
				_nextTry = DateTime.Now.Add(InteractionTiming.GetRetryInterval());
				return false;
			}

			// A visible save list blocks its parent menu even during cooldown.
			if (hasSave) return false;

			// 3) If Menu visible, open SaveData for deletion via Agent (robust - not affected by menu order)
			if (hasMenu && SeenStable("menu", StabilityShort))
			{
				// Open SaveData for deletion via Agent
				if (!_targetSlotObservedEmptyAfterDelete && !IsOnCooldown("menu-open-save") && DateTime.Now - _lastMenuFire >= InteractionTiming.GetRetryInterval())
				{
					SetStatus(_deleteSlotClickSent
						? $"{_dungeon.Name} Del: opening save data to verify slot"
						: $"{_dungeon.Name} Del: opening save data via Agent");
					_freshListRequested = DeepDungeonUi.OpenDeleteSaveViaAgent();
					_lastMenuFire = DateTime.Now;
					SetCooldown("menu-open-save", InteractionTiming.GetRetryInterval());
					_lastProgress = DateTime.Now;
					_nextTry = DateTime.Now.Add(InteractionTiming.GetRetryInterval());
					return false;
				}
			}

			// Finish only after the target slot was read empty and panels close.
			if (_targetSlotObservedEmptyAfterDelete && hasMenu)
			{
				SetStatus($"{_dungeon.Name} Del: closing entry windows");
				DeepDungeonUi.TryCloseAddon("DeepDungeonMenu");
				_nextTry = DateTime.Now.Add(InteractionTiming.GetRetryInterval());
				return false;
			}

			if (_targetSlotObservedEmptyAfterDelete && !hasSave && !hasMenu && !hasYesno && (DateTime.Now - _lastProgress) > TimeSpan.FromMilliseconds(400))
			{
				_npcNavHelper?.Cancel();
				SetStatus($"{_dungeon.Name} Del: finished");
				return true;
			}
			if (_targetSlotObservedEmptyAfterDelete) return false;

			// 4) Retry NPC interaction at the shared cadence while no UI is visible.
			if (!hasAnyUi && !IsOnCooldown("npc-probe"))
			{
				var inGrace = DateTime.Now < _postInteractUntil;
				var noWindowLongEnough = _noWindowSince != DateTime.MinValue && (DateTime.Now - _noWindowSince) >= InteractionTiming.GetRetryInterval();
				var allowFirstProbe = !_firstProbeDone;
				var preferNpcReprobe = _lastNpcNotFound != DateTime.MinValue;
				if (allowFirstProbe || preferNpcReprobe || (!inGrace && noWindowLongEnough))
				{
					SetStatus($"{_dungeon.Name} Del: interacting with NPC");
					InteractWithDungeonNpc();
					SetCooldown("npc-probe", InteractionTiming.GetRetryInterval());
					_firstProbeDone = true;
					_lastProgress = DateTime.Now;
					_nextTry = DateTime.Now.Add(InteractionTiming.GetRetryInterval());
					return false;
				}
			}

			_nextTry = DateTime.Now.AddMilliseconds(150);
			return false;

			bool DeleteVerificationExpired(string window) => _deleteSubmittedAt is { } sentAt &&
				Environment.TickCount64 - sentAt >= DeleteVerificationWindow.TotalMilliseconds &&
				SeenStable(window, DeleteVerificationWindow);

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

				var distance = NpcInteractionGuard.InteractionRange.Distance(player.Position, npc.Position);
				if (!NpcInteractionGuard.InteractionRange.HasArrived(player.Position, npc.Position))
				{
					if (!moveHelper.VNav.NavmeshReady())
					{
						SetStatus($"{_dungeon.Name} Del: waiting for navigation mesh");
						return;
					}
					var navState = _npcNavHelper?.Navigate(npc.Position, player.Position, NpcInteractionGuard.InteractionRange) ?? NavigationState.Failed;
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
				_postInteractUntil = DateTime.Now.Add(InteractionTiming.GetRetryInterval());
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
