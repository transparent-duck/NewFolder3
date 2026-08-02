using System.Numerics;
using FFXIVClientStructs.FFXIV.Client.Game.InstanceContent;

namespace DeepDungeon.Fsd.Dalamud.Runtime.Navigation
{
	public enum NavDriveResult
	{
		Moving,
		Staging,
		Arrived,
		StuckRetrying,
		Failed
	}

	/// <summary>
	/// Combines NavigationHelper (stuck detection, multi-try) with ProgressiveRoomNavigator
	/// (staged room-to-room routing) into a single Drive() call that returns a simplified result.
	/// Callers switch on 5 values instead of manually integrating NavigationState with room staging.
	///
	/// Tracks the current target to avoid reconfiguring ProgressiveRoomNavigator every frame,
	/// which would reset fallback progress set by TryHandleFailure's binary-search step-back.
	/// </summary>
	public sealed class NavigationDriver
	{
		private readonly NavigationHelper _navHelper;
		private readonly ProgressiveRoomNavigator _roomNav = new();

		private int? _lastTargetRoom;
		private Vector3 _lastDestination;
		private int _lastConfiguredPlayerRoom = int.MinValue;
		private bool _lastConfiguredHadDeepDungeon;
		private bool _configured;
		private bool _fallbackActive;

		public NavigationDriver(NavigationHelper navHelper)
		{
			_navHelper = navHelper;
		}

		public bool IsStaging => _roomNav.IsActive && _roomNav.IsStaging;
		public string StageLabel => _roomNav.StageLabel;
		public int StuckRetryCount => _navHelper.StuckRetryCount;

		/// <summary>
		/// Navigate to a destination with optional room-based staged routing.
		/// Call every frame. When targetRoom is non-null, ProgressiveRoomNavigator
		/// stages through intermediate rooms; when null, navigates directly.
		/// </summary>
		public unsafe NavDriveResult Drive(
			Vector3 destination,
			Vector3 playerPos,
			float arrivalRadius,
			InstanceContentDeepDungeon* dd,
			int playerRoom,
			int? targetRoom)
		{
			bool targetChanged = !_configured ||
								 targetRoom != _lastTargetRoom ||
								 Vector3.DistanceSquared(destination, _lastDestination) > 1.0f;
			bool semanticDestinationChanged =
				_configured &&
				targetRoom != _lastTargetRoom;
			bool playerRoomChanged = _configured && playerRoom != _lastConfiguredPlayerRoom;
			bool deepDungeonAvailabilityChanged = _configured && _lastConfiguredHadDeepDungeon != (dd != null);
			bool shouldConfigure =
				targetChanged ||
				(!_fallbackActive && !_roomNav.IsActive && (playerRoomChanged || deepDungeonAvailabilityChanged));

			if (shouldConfigure)
			{
				if (semanticDestinationChanged)
				{
					// targetRoom is part of the semantic destination. In particular,
					// passage room-center (targetRoom set) becoming the exact passage
					// actor (targetRoom null) must replace the running path even when
					// the two coordinates happen to be less than one metre apart.
					_navHelper.Cancel();
				}

				_lastTargetRoom = targetRoom;
				_lastDestination = destination;
				_lastConfiguredPlayerRoom = playerRoom;
				_lastConfiguredHadDeepDungeon = dd != null;
				_configured = true;
				_fallbackActive = false;
				_roomNav.Configure(dd, playerRoom, targetRoom, destination);
			}

			var activeDest = _roomNav.IsActive ? _roomNav.CurrentDestination : destination;
			var state = _navHelper.Navigate(activeDest, playerPos, arrivalRadius);

			switch (state)
			{
				case NavigationState.Moving:
					return NavDriveResult.Moving;

				case NavigationState.Arrived:
					if (_roomNav.IsActive &&
						!_roomNav.TryHandleArrival(dd, playerRoom, out _))
					{
						_lastConfiguredPlayerRoom = playerRoom;
						_lastConfiguredHadDeepDungeon = dd != null;
						_fallbackActive = true;
						return NavDriveResult.Staging;
					}
					_fallbackActive = false;
					_roomNav.Reset();
					return NavDriveResult.Arrived;

				case NavigationState.StuckRepathing:
					return NavDriveResult.StuckRetrying;

				case NavigationState.StuckGiveUp:
				case NavigationState.Failed:
					if (_roomNav.IsActive &&
						_roomNav.TryHandleFailure(dd, playerRoom, out _))
					{
						_fallbackActive = true;
						return NavDriveResult.Staging;
					}
					return NavDriveResult.Failed;

				default:
					return NavDriveResult.Moving;
			}
		}

		public void Cancel()
		{
			_navHelper.Cancel();
			_roomNav.Reset();
			_configured = false;
			_lastConfiguredPlayerRoom = int.MinValue;
			_lastConfiguredHadDeepDungeon = false;
			_fallbackActive = false;
		}

		public void Reset()
		{
			_navHelper.Reset();
			_roomNav.Reset();
			_configured = false;
			_lastConfiguredPlayerRoom = int.MinValue;
			_lastConfiguredHadDeepDungeon = false;
			_fallbackActive = false;
		}
	}
}
