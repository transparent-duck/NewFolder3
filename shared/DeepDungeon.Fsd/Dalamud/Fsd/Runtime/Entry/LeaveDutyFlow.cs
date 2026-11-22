using System;
using System.Collections.Generic;
using DeepDungeon.Fsd.Core;
using global::Dalamud.Plugin.Services;
using DeepDungeon.Fsd.Dalamud.Runtime;
using DeepDungeon.Fsd.Dalamud.GameState;
using DeepDungeon.Fsd.Dalamud.Runtime.Helpers;

namespace DeepDungeon.Fsd.Dalamud.Runtime.Entry
{
	/// <summary>
	/// Reactive leave-duty controller:
	/// - If SelectYesno (Abandon) exists: confirm 0
	/// - Else: request leave via ContentsFinderMenu agent event
	/// Repeats with stability/cooldowns until IsInDuty becomes false.
	/// </summary>
	public sealed class LeaveDutyFlow : IDisposable
	{
		private readonly bool _requireValidatedAbandonPrompt;
		private DungeonExitController? _controller;
		private RunContext? _ctx;
		private readonly Dictionary<string, DateTime> _cooldowns = new Dictionary<string, DateTime>();
		private readonly Dictionary<string, DateTime> _seenSince = new Dictionary<string, DateTime>();
		private DateTime _nextTry = DateTime.MinValue;
		private DateTime _lastProgress = DateTime.MinValue;
		private string _lastStatus = string.Empty;
		private DateTime _lastLog = DateTime.MinValue;
		private int _requestAttempts = 0;
		private DateTime _lastClear = DateTime.MinValue;

		private static readonly TimeSpan StabilityShort = TimeSpan.FromMilliseconds(150);

		public LeaveDutyFlow(bool requireValidatedAbandonPrompt = false)
		{
			_requireValidatedAbandonPrompt = requireValidatedAbandonPrompt;
		}

		public void Prepare(RunContext context)
		{
			_ctx = context;
			if (_requireValidatedAbandonPrompt)
			{
				_controller?.Dispose();
				_controller = new(returnToEntrance: false);
				_controller.Prepare(context);
				return;
			}
			_cooldowns.Clear();
			_seenSince.Clear();
			_nextTry = DateTime.MinValue;
			_lastProgress = DateTime.Now;
			_requestAttempts = 0;
			_lastClear = DateTime.MinValue;
			if (_ctx != null)
			{
				_ctx.StatusLine = "Leave: prepared";
				try { Service.Log.Info("[LeaveDutyFlow] Leave: prepared"); } catch { }
			}
		}

		public void Dispose()
		{
			_controller?.Dispose();
			_controller = null;
			_ctx = null;
		}

		public unsafe bool Update(IFramework framework)
		{
			if (_controller != null) return _controller.Update() == ExitOutcome.Completed;
			if (_ctx == null) return false;
			if (DutyState.IsClientTransitioning) return false;
			if (!_ctx.Duty.IsInDuty) return true;
			if (DateTime.Now < _nextTry) return false;

			var hasYesno = DeepDungeonUi.TryGetSelectYesNo(out var yesno);
			var hasTalk = DeepDungeonUi.TryGetTalk(out var talk);
			var hasSelect = DeepDungeonUi.TryGetSelectString(out var sel);
			MarkPresence("yesno", hasYesno);
			MarkPresence("talk", hasTalk);
			MarkPresence("select", hasSelect);

			// 1) If Abandon Yes/No is visible, confirm Yes
			if (hasYesno)
			{
				// Preserve the native exit dialog while it stabilizes; requesting another
				// leave here replaces PT100's save/reward confirmation with abandon.
				if (!SeenStable("yesno", StabilityShort) || IsOnCooldown("yesno"))
				{
					_nextTry = DateTime.Now.AddMilliseconds(150);
					return false;
				}
				if (_requireValidatedAbandonPrompt &&
				    !DeepDungeonUi.IsAbandonDutyConfirmationPrompt(yesno, out var promptError))
				{
					SetStatus($"Leave: waiting for unrelated confirmation ({promptError})");
					_nextTry = DateTime.Now.AddMilliseconds(300);
					return false;
				}

				SetStatus("Leave: confirming abandon");
				DeepDungeonUi.Fire(yesno, 0);
				SetCooldown("yesno", InteractionTiming.GetRetryInterval());
				_lastProgress = DateTime.Now;
				_nextTry = DateTime.Now.Add(InteractionTiming.GetRetryInterval());
				SetCooldown("request", InteractionTiming.GetRetryInterval());
				return false;
			}

			// 2) Clear blocking Talk/EventTalk (advance)
			if (hasTalk && SeenStable("talk", StabilityShort) && !IsOnCooldown("talk-advance"))
			{
				SetStatus("Leave: advancing dialog");
				DeepDungeonUi.Fire(talk, 0);
				SetCooldown("talk-advance", InteractionTiming.GetRetryInterval());
				_lastProgress = DateTime.Now;
				_nextTry = DateTime.Now.Add(InteractionTiming.GetRetryInterval());
				return false;
			}

			// 3) Clear unrelated SelectString (advance/close)
			if (hasSelect && SeenStable("select", StabilityShort) && !IsOnCooldown("select-advance"))
			{
				SetStatus("Leave: clearing SelectString");
				DeepDungeonUi.Fire(sel, 0);
				SetCooldown("select-advance", InteractionTiming.GetRetryInterval());
				_lastProgress = DateTime.Now;
				_nextTry = DateTime.Now.Add(InteractionTiming.GetRetryInterval());
				return false;
			}

			// 4) After several attempts, do a mild window clear and retry
			if (_requestAttempts >= 3 && DateTime.Now - _lastClear >= InteractionTiming.GetRetryInterval())
			{
				SetStatus("Leave: clearing stray windows");
				try
				{
					DeepDungeonUi.TryCloseAddon("Talk");
					DeepDungeonUi.TryCloseAddon("SelectString");
					DeepDungeonUi.TryCloseAddon("ContextIconMenu");
				}
				catch { }
				_lastClear = DateTime.Now;
				// Observe pending leave requests before the next window-clear retry.
				_nextTry = DateTime.Now.AddMilliseconds(150);
				return false;
			}

			// 2) Otherwise request leave via agent
			if (!IsOnCooldown("request"))
			{
				if (LeaveDutyHelper.TryRequestLeaveDuty())
				{
					SetStatus($"Leave: requested leave (attempt {_requestAttempts + 1})");
					_lastProgress = DateTime.Now;
				}
				_requestAttempts++;
				SetCooldown("request", InteractionTiming.GetRetryInterval());
				_nextTry = DateTime.Now.Add(InteractionTiming.GetRetryInterval());
				return false;
			}

			_nextTry = DateTime.Now.AddMilliseconds(150);
			return false;

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
			void SetStatus(string s)
			{
				if (_ctx == null) return;
				_ctx.StatusLine = s;
				if (!string.Equals(_lastStatus, s, StringComparison.Ordinal) || (DateTime.Now - _lastLog).TotalSeconds >= 2)
				{
					_lastStatus = s;
					_lastLog = DateTime.Now;
					try { Service.Log.Info($"[LeaveDutyFlow] {s}"); } catch { }
				}
			}
		}
	}
}
