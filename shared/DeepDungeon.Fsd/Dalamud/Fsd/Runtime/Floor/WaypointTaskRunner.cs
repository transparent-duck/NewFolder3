using System;
using System.Numerics;
using DeepDungeon.Fsd.Dalamud.Runtime.Navigation;

namespace DeepDungeon.Fsd.Dalamud.Runtime.Floor
{
	public enum TaskPhase
	{
		Idle,
		Traveling,
		WaitingPre,
		WaitingPost
	}

	public enum TaskResult
	{
		InProgress,
		Arrived,
		Complete,
		TimedOut,
		NavigationFailed
	}

	/// <summary>
	/// Generic waypoint task runner: navigate to a position, optionally wait for a
	/// precondition, then wait for a postcondition. Different interaction types
	/// (traps, chests, holds) are expressed as different condition functions rather
	/// than separate FSM states.
	/// </summary>
	public sealed class WaypointTaskRunner
	{
		private readonly NavigationHelper _navHelper;
		private TaskPhase _phase = TaskPhase.Idle;
		private DateTime _phaseEntryTime;

		private Vector3 _target;
		private float _arrivalRadius;
		private Func<bool>? _preCondition;
		private float _preTimeoutSeconds;
		private Func<double, bool> _postCondition = null!;
		private float _postTimeoutSeconds;

		public WaypointTaskRunner(NavigationHelper navHelper)
		{
			_navHelper = navHelper;
		}

		public TaskPhase Phase => _phase;
		public int NavigationIssueCount => _navHelper.NavigationIssueCount;

		public double ElapsedSeconds => _phase == TaskPhase.Idle
			? 0
			: (DateTime.Now - _phaseEntryTime).TotalSeconds;

		public void Configure(
			Vector3 target,
			float arrivalRadius,
			Func<bool>? preCondition,
			float preTimeoutSeconds,
			Func<double, bool> postCondition,
			float postTimeoutSeconds)
		{
			_target = target;
			_arrivalRadius = arrivalRadius;
			_preCondition = preCondition;
			_preTimeoutSeconds = preTimeoutSeconds;
			_postCondition = postCondition;
			_postTimeoutSeconds = postTimeoutSeconds;
			_navHelper.Cancel();
			_navHelper.BeginIssueTrackingScope();
			SetPhase(TaskPhase.Traveling);
		}

		public TaskResult Update(Vector3 playerPos)
		{
			return _phase switch
			{
				TaskPhase.Traveling => UpdateTraveling(playerPos),
				TaskPhase.WaitingPre => UpdateWaitingPre(),
				TaskPhase.WaitingPost => UpdateWaitingPost(),
				_ => TaskResult.InProgress,
			};
		}

		public void Reset(bool cancelNavigation = true)
		{
			_phase = TaskPhase.Idle;
			if (cancelNavigation)
				_navHelper.Cancel();
		}

		private TaskResult UpdateTraveling(Vector3 playerPos)
		{
			var state = _navHelper.Navigate(_target, playerPos, _arrivalRadius);

			switch (state)
			{
				case NavigationState.Arrived:
					_navHelper.Cancel();
					return TransitionFromArrival();

				case NavigationState.StuckGiveUp:
				case NavigationState.Failed:
					_phase = TaskPhase.Idle;
					return TaskResult.NavigationFailed;

				default:
					return TaskResult.InProgress;
			}
		}

		private TaskResult TransitionFromArrival()
		{
			if (_preCondition != null && !_preCondition())
			{
				SetPhase(TaskPhase.WaitingPre);
				return TaskResult.Arrived;
			}

			SetPhase(TaskPhase.WaitingPost);
			return TaskResult.Arrived;
		}

		private TaskResult UpdateWaitingPre()
		{
			if (_preCondition == null || _preCondition())
			{
				SetPhase(TaskPhase.WaitingPost);
				return TaskResult.InProgress;
			}

			double elapsed = (DateTime.Now - _phaseEntryTime).TotalSeconds;
			if (elapsed >= _preTimeoutSeconds)
			{
				_phase = TaskPhase.Idle;
				return TaskResult.TimedOut;
			}

			return TaskResult.InProgress;
		}

		private TaskResult UpdateWaitingPost()
		{
			double elapsed = (DateTime.Now - _phaseEntryTime).TotalSeconds;

			if (_postCondition(elapsed))
			{
				_phase = TaskPhase.Idle;
				return TaskResult.Complete;
			}

			if (elapsed >= _postTimeoutSeconds)
			{
				_phase = TaskPhase.Idle;
				return TaskResult.TimedOut;
			}

			return TaskResult.InProgress;
		}

		private void SetPhase(TaskPhase phase)
		{
			_phase = phase;
			_phaseEntryTime = DateTime.Now;
		}
	}
}
