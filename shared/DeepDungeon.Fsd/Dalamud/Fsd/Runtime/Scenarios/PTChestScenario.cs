using System;
using DeepDungeon.Fsd.Core;
using global::Dalamud.Plugin.Services;
using DeepDungeon.Fsd.Dalamud.GameState;
using DeepDungeon.Fsd.Dalamud.Runtime;
using DeepDungeon.Fsd.Dalamud.Runtime.Entry;

namespace DeepDungeon.Fsd.Dalamud.Runtime.Scenarios
{
	/// <summary>
	/// PT: create save at floor X, dig for banded hoards only, defeat boss, exit, delete save, and loop.
	/// </summary>
	public sealed class PTChestScenario : IScenario
	{
		private readonly int _startFloor;
		private readonly string _name;

		// Scenario-owned configuration
		private int _slotIndex = -1;

		private RunContext? _ctx;
		private GenericEntryFlow? _entry;
		private bool _complete;
		private bool _wasEverInDuty;
		private GenericDeleteSaveFlow? _deleter;
		private int _createdSlotIndex = -1;
		private PilgrimsTraverseRestExitFlow? _restExitFlow;
		private bool _restExitComplete;

		public PTChestScenario(int startFloor = 31)
		{
			_startFloor = startFloor;
			_name = $"Pilgrim's Traverse {startFloor}-{startFloor + 9}";
		}

		public string Name => _name;
		public bool IsComplete => _complete;
		public bool ShouldLoop => true;
		public bool RequiresDutyCompletionEvent => true;

		public void Initialize(RunContext context)
		{
			_ctx = context;
			_ctx.ResetAttemptState();

			// Actual emptiness is only observable after the entry flow opens DeepDungeonSaveData.
			_slotIndex = 0;
			_entry = new GenericEntryFlow(DungeonCatalog.PilgrimsTraverse, _startFloor, _slotIndex);
			_entry.Prepare(context);
			_complete = false;
			_wasEverInDuty = false;
			_deleter = null;
			_createdSlotIndex = -1;
			_restExitFlow = null;
			_restExitComplete = false;
		}

		public void Update(IFramework framework)
		{
			var ctx = _ctx;
			if (ctx == null) return;

			var inTargetDuty =
				ctx.Duty.IsInDuty &&
				ctx.Duty.DungeonId == 4 &&
				ctx.Duty.Floor > 0 &&
				!ctx.Duty.IsTransitioning;
			var postDutyDecision = ScenarioPostDutyPlanner.Decide(new ScenarioPostDutySnapshot
			{
				WasEverInDuty = _wasEverInDuty,
				IsInDuty = inTargetDuty,
				IsTransitioning = ctx.Duty.IsTransitioning,
				StatusIsError = ctx.StatusIsError,
				DutyCompletionObserved = ctx.DutyCompletionObserved,
				DutyFailureObserved = ctx.DutyFailureObserved
			});

			switch (postDutyDecision.Action)
			{
				case ScenarioPostDutyAction.CompleteBeforeEntryError:
					_complete = true;
					return;
				case ScenarioPostDutyAction.ContinueEntry:
					if (_entry != null && _entry.Update(framework))
					{
						// wait for duty detection
					}
					return;
				case ScenarioPostDutyAction.WaitForTransition:
					return;
				case ScenarioPostDutyAction.RunCleanup:
					UpdatePostDutyCleanup(framework, ctx, postDutyDecision.Outcome);
					return;
				case ScenarioPostDutyAction.WaitForDutyExit:
					break;
			}

			_wasEverInDuty = true;

			if (ctx.StatusIsError)
			{
				return;
			}

			if (_entry != null && !_entry.CleanupAfterDutyEntry())
			{
				return;
			}

			// Capture created slot index once we know we're in duty
			if (_createdSlotIndex < 0)
			{
				_createdSlotIndex = ctx.SaveSlots.LastUsedSlotIndex;
			}

			// FloorPhaseController handles passage navigation automatically when conditions are met.
		}

		private void UpdatePostDutyCleanup(IFramework framework, RunContext ctx, ScenarioRunOutcome outcome)
		{
			if (!_restExitComplete)
			{
				_restExitFlow ??= new PilgrimsTraverseRestExitFlow();
				if (!_restExitFlow.IsPrepared)
				{
					_restExitFlow.Prepare(ctx);
				}
				if (!_restExitFlow.Update(framework))
				{
					return;
				}
				_restExitComplete = true;
			}

			// Lazily create delete flow once we know the slot index
			if (_deleter == null)
			{
				// If GenericEntryFlow was used, LastUsedSlotIndex should be the slot it created.
				if (_createdSlotIndex < 0)
				{
					_createdSlotIndex = ctx.SaveSlots.LastUsedSlotIndex;
				}
				if (_createdSlotIndex >= 0 && DungeonCatalog.PilgrimsTraverse.NpcDataId != 0)
				{
					_deleter = new GenericDeleteSaveFlow(DungeonCatalog.PilgrimsTraverse, _createdSlotIndex);
					_deleter.Prepare(ctx);
				}
				else
				{
					ctx.StatusLine = $"{Name}: cannot identify created save slot for cleanup.";
					ctx.StatusIsError = true;
					_complete = true;
					return;
				}
			}

			if (_deleter != null && _deleter.Update(framework))
			{
				_deleter = null;
				FinalizePostDutyRun(ctx, outcome);
			}
		}

		private void FinalizePostDutyRun(RunContext ctx, ScenarioRunOutcome outcome)
		{
			if (outcome == ScenarioRunOutcome.Completed)
			{
				_complete = true;
				return;
			}

			ctx.StatusLine = outcome == ScenarioRunOutcome.Failed
				? $"{Name}: Deep Dungeon run failed; save cleanup completed and loop will not be counted."
				: $"{Name}: duty ended before completion; save cleanup completed and loop will not be counted.";
			ctx.StatusIsError = true;
			_complete = true;
		}

		public void Dispose()
		{
			try { _entry?.Reset(); } catch { }
			try { _restExitFlow?.Dispose(); } catch { }
		}
	}
}
