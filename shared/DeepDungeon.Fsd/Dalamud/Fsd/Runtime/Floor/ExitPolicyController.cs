using System;
using global::Dalamud.Plugin.Services;
using DeepDungeon.Fsd.Dalamud.Runtime;
using DeepDungeon.Fsd.Dalamud.Runtime.Entry;

namespace DeepDungeon.Fsd.Dalamud.Runtime.Floor
{
	/// <summary>
	/// Applies leave behavior based on RunOptions.LeaveMode.
	/// Starts LeaveDutyFlow when the configured condition is met.
	/// </summary>
	public sealed class ExitPolicyController
	{
		private RunContext? _ctx;
		private LeaveDutyFlow? _leaveFlow;
		private DateTime _startTime = DateTime.MinValue;
		private int _startHoardCount = 0;
		private bool _hasStartHoardCount;

		public bool IsLeaveActive => _leaveFlow != null;

		public void Initialize(RunContext context)
		{
			_ctx = context;
			_leaveFlow = null;
			_startTime = DateTime.Now;
			_hasStartHoardCount = TryReadHoardCount(out _startHoardCount);
		}

		public void Evaluate()
		{
			if (_ctx == null || _leaveFlow != null || !_ctx.Duty.IsInDuty) return;
			if (_ctx.Duty.IsTransitioning) return;
			switch (_ctx.RunOptions.Current.LeaveMode)
			{
				case LeaveMode.Immediate:
					TriggerLeaveFlow();
					break;
				case LeaveMode.AfterHoard:
					// Trigger leave once we have obtained at least one more hoard than at start
					if (!TryReadHoardCount(out var current))
						break;

					if (!_hasStartHoardCount)
					{
						_startHoardCount = current;
						_hasStartHoardCount = true;
						break;
					}

					if (current >= _startHoardCount + 1)
						TriggerLeaveFlow();
					break;
				case LeaveMode.AfterFinishDungeon:
					if (_ctx.DutyCompletionObserved && !_ctx.DutyFailureObserved)
					{
						TriggerLeaveFlow();
					}
					break;
				case LeaveMode.OnBossFloorEntry:
					if (_ctx.Duty.IsBossFloor)
					{
						TriggerLeaveFlow();
					}
					break;
				case LeaveMode.AfterNMinutes:
					var minutes = Math.Max(1, _ctx.RunOptions.Current.LeaveAfterMinutes);
					if (_startTime != DateTime.MinValue && DateTime.Now - _startTime >= TimeSpan.FromMinutes(minutes))
					{
						TriggerLeaveFlow();
					}
					break;
				default:
					// Defer to scenario logic
					break;
			}
		}

		public void Update(IFramework framework)
		{
			_leaveFlow?.Update(framework);
		}

		public void Dispose()
		{
			_leaveFlow = null;
			_ctx = null;
			_startTime = DateTime.MinValue;
			_startHoardCount = 0;
			_hasStartHoardCount = false;
		}

		private bool TryReadHoardCount(out int count)
		{
			count = 0;
			try
			{
				if (_ctx == null || !_ctx.Duty.IsInDuty || _ctx.Duty.StateReadFailed)
					return false;

				count = Math.Max(0, _ctx.Duty.HoardCount);
				return true;
			}
			catch (Exception ex)
			{
				Service.Log.Error($"[ExitPolicy] Failed to read hoard count: {ex}");
				return false;
			}
		}

		private void TriggerLeaveFlow()
		{
			if (_ctx == null) return;
			if (_leaveFlow == null)
			{
				_leaveFlow = new LeaveDutyFlow(_ctx.RunOptions.Current.RequireValidatedAbandonPrompt);
				_leaveFlow.Prepare(_ctx);
			}
		}
	}
}

