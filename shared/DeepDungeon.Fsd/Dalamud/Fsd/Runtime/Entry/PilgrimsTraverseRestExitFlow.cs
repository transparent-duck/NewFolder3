using System;
using System.Numerics;
using global::Dalamud.Game.ClientState.Objects.Types;
using global::Dalamud.Plugin.Services;
using DeepDungeon.Fsd.Dalamud.Runtime;
using DeepDungeon.Fsd.Dalamud.GameState;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using DeepDungeon.Fsd.Dalamud.Runtime.Navigation;
using DeepDungeon.Fsd.Core;

namespace DeepDungeon.Fsd.Dalamud.Runtime.Entry
{
	/// <summary>
	/// Handles the special PT rest room that appears after leaving duty by interacting with the exit event object.
	/// </summary>
	public sealed class PilgrimsTraverseRestExitFlow : IDisposable
	{
		private const uint RestExitEventObjDataId = 2014757;

		private RunContext? _ctx;
		private DateTime _nextTry = DateTime.MinValue;
		private string _lastStatus = string.Empty;
		private DateTime _lastLog = DateTime.MinValue;
		private bool _everSawExit;
		private bool _exitConfirmed;
		private bool _finished;
		private NavigationHelper? _navHelper;
		private DateTime _nextInteractTry = DateTime.MinValue;
		private readonly bool _requireValidatedConfirmation;
		private DateTime _controlledExitInteractionAt = DateTime.MinValue;
		private const uint RestExitWarpRowId = 131612;
		private const int ControlledPromptOwnershipWindowMilliseconds = 2000;

		public PilgrimsTraverseRestExitFlow(bool requireValidatedConfirmation = false)
		{
			_requireValidatedConfirmation = requireValidatedConfirmation;
		}

		public void Prepare(RunContext context)
		{
			_ctx = context;
			_nextTry = DateTime.MinValue;
			_lastStatus = "PT: rest exit ready";
			_lastLog = DateTime.MinValue;
			_everSawExit = false;
			_exitConfirmed = false;
			_finished = false;
			_navHelper = new NavigationHelper(context.Navigator);
			_nextInteractTry = DateTime.MinValue;
			_controlledExitInteractionAt = DateTime.MinValue;
			SetStatus("PT: rest exit ready");
		}

		public bool IsPrepared => _ctx != null;

		public unsafe bool Update(IFramework framework)
		{
			if (_ctx == null || _finished)
				return true;

			if (DateTime.Now < _nextTry)
				return false;

			if (!_ctx.Duty.IsInDuty &&
			    NpcInteractionGuard.FindByBaseId(DungeonCatalog.PilgrimsTraverse.NpcDataId) is { IsTargetable: true })
			{
				SetFinished("PT: rest exit complete at entry NPC");
				return true;
			}

			var hasYesno = DeepDungeonUi.TryGetSelectYesNo(out var yesno);

			if (hasYesno)
			{
				if (_requireValidatedConfirmation)
				{
					bool deletePrompt = DeepDungeonUi.IsDeleteSaveConfirmationPrompt(yesno, out _);
					bool promptReadable = DeepDungeonUi.TryGetConfirmationPromptText(yesno, out string actualPrompt, out string promptError);
					string expectedPrompt = Service.DataManager
						.GetExcelSheet<Lumina.Excel.Sheets.Warp>()?
						.GetRow(RestExitWarpRowId)
						.Question
						.ExtractText() ?? string.Empty;
					int elapsedMilliseconds = _controlledExitInteractionAt == DateTime.MinValue
						? int.MaxValue
						: (int)Math.Clamp(
							(DateTime.UtcNow - _controlledExitInteractionAt).TotalMilliseconds,
							0,
							int.MaxValue);
					var decision = ControlledPtSurveyPolicy.DecideRestExitPrompt(
						deletePrompt,
						_controlledExitInteractionAt != DateTime.MinValue,
						elapsedMilliseconds,
						ControlledPromptOwnershipWindowMilliseconds,
						expectedPrompt,
						promptReadable ? actualPrompt : string.Empty);
					if (decision != ControlledPtRestPromptDecision.Accept)
					{
						DeepDungeonUi.TryCloseAddon("SelectYesno");
						_ctx.StatusLine = decision == ControlledPtRestPromptDecision.RejectDeletePrompt
							? "Controlled PT rest exit stopped: delete-save confirmation was open."
							: $"Controlled PT rest exit stopped: prompt validation failed ({decision}; {promptError}).";
						_ctx.StatusIsError = true;
						return false;
					}
				}

				SetStatus("PT: confirming rest exit");
				DeepDungeonUi.Fire(yesno, 0);
				_exitConfirmed = _requireValidatedConfirmation || _everSawExit;
				_controlledExitInteractionAt = DateTime.MinValue;
				_nextTry = DateTime.Now.AddMilliseconds(350);
				return false;
			}

			var exitObj = FindExitObject();
			if (exitObj == null)
			{
				_navHelper?.Reset();

				if (_everSawExit && _exitConfirmed)
				{
					SetFinished("PT: rest room cleared");
					return true;
				}

				SetStatus(_everSawExit
					? "PT: waiting for rest exit confirmation"
					: "PT: waiting for rest exit actor");
				_nextTry = DateTime.Now.AddMilliseconds(250);
				return false;
			}

			_everSawExit = true;
			var player = Service.LocalPlayer;
			if (player == null)
			{
				_nextTry = DateTime.Now.AddMilliseconds(250);
				return false;
			}

			var exitPos = exitObj.Position;
			var playerPos = player.Position;
			var navState = _navHelper?.Navigate(exitPos, playerPos, 1.2f) ?? NavigationState.Arrived;

			if (navState == NavigationState.Failed || navState == NavigationState.StuckGiveUp)
			{
				SetStatus("PT: rest exit navigation failed");
				_nextTry = DateTime.Now.AddMilliseconds(400);
				return false;
			}

			var distSq = Vector3.DistanceSquared(playerPos, exitPos);
			const float interactRange = 2.0f;
			if (distSq <= interactRange * interactRange && DateTime.Now >= _nextInteractTry)
			{
				if (TryInteract(exitObj))
				{
					if (_requireValidatedConfirmation)
						_controlledExitInteractionAt = DateTime.UtcNow;
					SetStatus("PT: interacting with rest exit");
				}
				_nextInteractTry = DateTime.Now.AddMilliseconds(600);
			}
			_nextTry = DateTime.Now.AddMilliseconds(500);
			return false;
		}

		private unsafe bool TryInteract(IGameObject obj)
		{
			var ts = TargetSystem.Instance();
			if (ts == null)
				return false;

			var native = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)obj.Address;
			if (native == null)
				return false;

			if (ts->Target != native)
			{
				ts->Target = native;
			}

			ts->InteractWithObject(native);
			return true;
		}

		private IGameObject? FindExitObject()
		{
			foreach (var obj in Service.GameObjects)
			{
				if (obj == null)
					continue;

				if (obj.BaseId == RestExitEventObjDataId)
					return obj;
			}

			return null;
		}

		private void SetFinished(string status)
		{
			_finished = true;
			try { _navHelper?.Cancel(); } catch { }
			SetStatus(status);
		}

		public void Dispose()
		{
			_finished = true;
			try { _navHelper?.Cancel(); } catch { }
			_navHelper = null;
			_ctx = null;
		}

		private void SetStatus(string status)
		{
			if (_ctx == null)
				return;

			_ctx.StatusLine = status;
			if (string.Equals(_lastStatus, status, StringComparison.Ordinal) && (DateTime.Now - _lastLog).TotalSeconds < 2)
				return;

			_lastStatus = status;
			_lastLog = DateTime.Now;
			try { Service.Log.Info($"[PTRestExit] {status}"); } catch { }
		}
	}
}
