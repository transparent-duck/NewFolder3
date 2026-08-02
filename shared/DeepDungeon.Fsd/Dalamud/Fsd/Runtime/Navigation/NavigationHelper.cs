using System;
using System.Numerics;

namespace DeepDungeon.Fsd.Dalamud.Runtime.Navigation
{
	/// <summary>
	/// Unified navigation helper that combines multi-try (interruption recovery)
	/// and stuck detection (bad pathfinding recovery) for robust VNav usage.
	/// Handles both cases where VNav is interrupted and where VNav generates invalid paths.
	/// </summary>
	public sealed class NavigationHelper
	{
		private readonly INavigator _navigator;
		private readonly Func<bool> _isPathRunning;
		private readonly Func<DateTime> _clock;
		private readonly Action<string> _logInfo;
		private readonly Action<string> _logWarning;

		// Multi-try state (interruption recovery)
		private DateTime _nextNavCheck = DateTime.MinValue;
		private Vector3 _targetDest = Vector3.Zero;
		private bool _hasTarget = false;

		// Stuck detection state (bad pathfinding recovery)
		private Vector3 _lastPos;
		private DateTime _lastMoveCheckAt = DateTime.MinValue;
		private int _stuckCount = 0;
		private int _navigationIssueCount;
		private bool _initialDispatchIssued;

		public NavigationHelper(
			INavigator navigator,
			Func<bool>? isPathRunning = null,
			Func<DateTime>? clock = null,
			Action<string>? logInfo = null,
			Action<string>? logWarning = null)
		{
			_navigator = navigator ?? throw new ArgumentNullException(nameof(navigator));
			_isPathRunning = isPathRunning ?? DeepDungeon.Fsd.Dalamud.moveHelper.VNav.Path.IsRunning;
			_clock = clock ?? (() => DateTime.Now);
			_logInfo = logInfo ?? (message => Service.Log.Info(message));
			_logWarning = logWarning ?? (message => Service.Log.Warning(message));
		}

		/// <summary>
		/// Starts a new waypoint-scoped issue counter. Ordinary navigation reset and
		/// arrival deliberately preserve the counter until the next scope starts.
		/// </summary>
		public void BeginIssueTrackingScope()
		{
			_navigationIssueCount = 0;
			_initialDispatchIssued = false;
		}

		/// <summary>
		/// Request navigation to destination. Handles both multi-try and stuck detection.
		/// Call this every frame/tick - it handles throttling internally.
		/// </summary>
		/// <param name="dest">Target destination</param>
		/// <param name="playerPos">Current player position (for stuck detection)</param>
		/// <param name="arrivalRadius">Distance threshold to consider "arrived" (default 1.2m)</param>
		/// <returns>NavigationState indicating current status</returns>
		public NavigationState Navigate(Vector3 dest, Vector3 playerPos, float arrivalRadius = 1.2f, double retryIntervalSeconds = 1.0)
		{
			var now = _clock();
			if (retryIntervalSeconds < 0.1)
			{
				retryIntervalSeconds = 0.1;
			}

			// Check if arrived
			float distSq = Vector3.DistanceSquared(playerPos, dest);
			if (distSq <= arrivalRadius * arrivalRadius)
			{
				if (_hasTarget)
				{
					try { _navigator.CancelAll(); } catch { }
				}
				Reset();
				return NavigationState.Arrived;
			}

			// Check if target changed significantly (>1m)
			bool targetChanged = !_hasTarget ||
								 Vector3.DistanceSquared(_targetDest, dest) > 1.0f;

			if (targetChanged)
			{
				_targetDest = dest;
				_hasTarget = true;
				_nextNavCheck = DateTime.MinValue; // Force immediate navigation attempt
				_lastPos = playerPos;
				_lastMoveCheckAt = now;
				_stuckCount = 0;
			}

			// Throttled multi-try check (every 1s)
			if (now >= _nextNavCheck)
			{
				_nextNavCheck = now.AddSeconds(retryIntervalSeconds);

				bool isNavigating = _isPathRunning();

				if (!isNavigating)
				{
					// VNav not navigating - (re)issue navigation
					if (_initialDispatchIssued)
						_navigationIssueCount++;
					_initialDispatchIssued = true;
					bool success = _navigator.PathfindAndMoveTo(dest, false);
					if (!success)
					{
						return NavigationState.Failed;
					}

				}
			}

			// Stuck detection (continuous monitoring)
			var dx = playerPos.X - _lastPos.X;
			var dz = playerPos.Z - _lastPos.Z;
			var moved2 = dx * dx + dz * dz;

			if (moved2 > 0.25f) // Moved > 0.5m
			{
				_lastPos = playerPos;
				_lastMoveCheckAt = now;
				_stuckCount = 0;
				return NavigationState.Moving;
			}

			// Not moving - check if stuck
			if ((now - _lastMoveCheckAt).TotalSeconds >= 3.0)
			{
				_stuckCount++;

				if (_stuckCount >= 3) // Failed 3 times
				{
					_logWarning($"[NavigationHelper] Stuck 3 times, giving up on destination ({dest.X:F1}, {dest.Y:F1}, {dest.Z:F1})");
					Reset();
					return NavigationState.StuckGiveUp;
				}

				// Repath
				_navigationIssueCount++;
				_logInfo($"[NavigationHelper] Stuck detected (attempt {_stuckCount}/3), repathing to ({dest.X:F1}, {dest.Y:F1}, {dest.Z:F1})");
				_navigator.RepathTo(dest, false);
				_lastPos = playerPos;
				_lastMoveCheckAt = now;
				_nextNavCheck = DateTime.MinValue; // Force immediate multi-try check after repath
				return NavigationState.StuckRepathing;
			}

			return NavigationState.Moving;
		}

		/// <summary>
		/// Reset navigation state (call when canceling or task complete)
		/// </summary>
		public void Reset()
		{
			_hasTarget = false;
			_nextNavCheck = DateTime.MinValue;
			_stuckCount = 0;
			_lastMoveCheckAt = DateTime.MinValue;
		}

		/// <summary>
		/// Cancel current navigation and reset state
		/// </summary>
		public void Cancel()
		{
			try { _navigator.CancelAll(); } catch { }
			Reset();
		}

		/// <summary>
		/// Returns true if currently has an active navigation target
		/// </summary>
		public bool HasActiveTarget => _hasTarget;

		/// <summary>
		/// Returns the current stuck retry count (0-3)
		/// </summary>
		public int StuckRetryCount => _stuckCount;

		/// <summary>
		/// Monotonic count of VNav reissues and stuck repaths in the current
		/// waypoint scope. Preserved across arrival and Reset().
		/// </summary>
		public int NavigationIssueCount => _navigationIssueCount;
	}

	/// <summary>
	/// Navigation state returned by NavigationHelper.Navigate()
	/// </summary>
	public enum NavigationState
	{
		/// <summary>Moving toward destination normally</summary>
		Moving,

		/// <summary>Arrived at destination (within threshold)</summary>
		Arrived,

		/// <summary>Stuck detected, repathing in progress</summary>
		StuckRepathing,

		/// <summary>Stuck 3 times, gave up on this destination</summary>
		StuckGiveUp,

		/// <summary>VNav pathfinding failed to start</summary>
		Failed
	}
}
