using DeepDungeon.Fsd.Core;
using global::Dalamud.Plugin.Services;
using DeepDungeon.Fsd.Dalamud.GameState;
using DeepDungeon.Fsd.Dalamud.Runtime;
using DeepDungeon.Fsd.Dalamud.Runtime.Entry;

namespace DeepDungeon.Fsd.Dalamud.Runtime.Scenarios
{
	// [Under Development] Generic scenario for PotD, HoH, and EO.
	// Auto-entry is only functional when DungeonCatalog NpcDataId is filled;
	// currently only PT has complete metadata.
	public sealed class GenericDungeonScenario : IScenario
	{
		private readonly DungeonData _dungeon;
		private readonly int _desiredStartFloor;

		public string Name { get; }

		private int _slotIndex = -1;
		private RunContext? _ctx;
		private GenericEntryFlow? _entry;
		private bool _complete;
		private bool _wasEverInDuty;
		private GenericDeleteSaveFlow? _deleter;
		private int _createdSlotIndex = -1;
		private PilgrimsTraverseRestExitFlow? _ptRestExitFlow;
		private bool _ptRestExitComplete;

		public bool IsComplete => _complete;
		public bool ShouldLoop => true;
		public bool RequiresDutyCompletionEvent => true;

		public GenericDungeonScenario(DungeonData dungeon, string name, int desiredStartFloor = 1)
		{
			_dungeon = dungeon;
			Name = name;
			_desiredStartFloor = desiredStartFloor;
		}

		public void Initialize(RunContext context)
		{
			_ctx = context;
			_ctx.ResetAttemptState();
			_complete = false;
			_wasEverInDuty = false;
			_deleter = null;
			_createdSlotIndex = -1;
			_ptRestExitFlow = null;
			_ptRestExitComplete = false;

			if (_dungeon.NpcDataId != 0)
			{
				_slotIndex = 0;
				_entry = new GenericEntryFlow(_dungeon, _desiredStartFloor, _slotIndex);
				_entry.Prepare(context);
			}
			else
			{
				_entry = null;
			}
		}

		public void Update(IFramework framework)
		{
			if (_ctx == null) return;

			var inTargetDuty =
				_ctx.Duty.IsInDuty &&
				_ctx.Duty.Floor > 0 &&
				!_ctx.Duty.IsTransitioning &&
				(_dungeon.DutyDungeonId == 0 || _ctx.Duty.DungeonId == _dungeon.DutyDungeonId);
			var postDutyDecision = ScenarioPostDutyPlanner.Decide(new ScenarioPostDutySnapshot
			{
				WasEverInDuty = _wasEverInDuty,
				IsInDuty = inTargetDuty,
				IsTransitioning = _ctx.Duty.IsTransitioning,
				StatusIsError = _ctx.StatusIsError,
				DutyCompletionObserved = _ctx.DutyCompletionObserved,
				DutyFailureObserved = _ctx.DutyFailureObserved
			});

			switch (postDutyDecision.Action)
			{
				case ScenarioPostDutyAction.CompleteBeforeEntryError:
					_complete = true;
					return;
				case ScenarioPostDutyAction.ContinueEntry:
					if (_entry != null)
					{
						_entry.Update(framework);
						return;
					}

					MarkAutoEntryUnsupported();
					return;
				case ScenarioPostDutyAction.WaitForTransition:
					return;
				case ScenarioPostDutyAction.RunCleanup:
					UpdatePostDutyCleanup(framework, _ctx, postDutyDecision.Outcome);
					return;
				case ScenarioPostDutyAction.WaitForDutyExit:
					break;
			}

			_wasEverInDuty = true;

			if (_ctx.StatusIsError)
			{
				return;
			}

			if (_entry != null && !_entry.CleanupAfterDutyEntry())
				return;

			if (_createdSlotIndex < 0)
			{
				_createdSlotIndex = _ctx.SaveSlots.LastUsedSlotIndex;
			}
		}

		public void Dispose()
		{
			try { _entry?.Reset(); } catch { }
			try { _ptRestExitFlow?.Dispose(); } catch { }
		}

		private void MarkAutoEntryUnsupported()
		{
			if (_ctx != null)
			{
				_ctx.StatusLine = $"{Name}: auto-entry metadata is missing for {_dungeon.Name}. Start inside the duty or add DungeonCatalog entry metadata.";
				_ctx.StatusIsError = true;
			}

			_complete = true;
		}

		private void UpdatePostDutyCleanup(IFramework framework, RunContext ctx, ScenarioRunOutcome outcome)
		{
			if (_dungeon.DungeonId == DungeonCatalog.PilgrimsTraverse.DungeonId && !_ptRestExitComplete)
			{
				_ptRestExitFlow ??= new PilgrimsTraverseRestExitFlow();
				if (!_ptRestExitFlow.IsPrepared)
				{
					_ptRestExitFlow.Prepare(ctx);
				}
				if (!_ptRestExitFlow.Update(framework))
				{
					return;
				}
				_ptRestExitComplete = true;
			}

			if (!CanDeleteCreatedSave(ctx))
			{
				FinalizePostDutyRun(ctx, outcome);
				return;
			}

			if (_deleter == null)
			{
				_deleter = new GenericDeleteSaveFlow(_dungeon, _createdSlotIndex);
				_deleter.Prepare(ctx);
			}

			if (_deleter.Update(framework))
			{
				_deleter = null;
				FinalizePostDutyRun(ctx, outcome);
			}
		}

		private bool CanDeleteCreatedSave(RunContext ctx)
		{
			if (_entry == null || _dungeon.NpcDataId == 0)
			{
				return false;
			}

			if (_createdSlotIndex < 0)
			{
				_createdSlotIndex = ctx.SaveSlots.LastUsedSlotIndex;
			}

			if (_createdSlotIndex >= 0)
			{
				return true;
			}

			ctx.StatusLine = $"{Name}: cannot identify created save slot for cleanup.";
			ctx.StatusIsError = true;
			_complete = true;
			return false;
		}

		private void FinalizePostDutyRun(RunContext ctx, ScenarioRunOutcome outcome)
		{
			if (ctx.StatusIsError)
			{
				_complete = true;
				return;
			}

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
	}
}
