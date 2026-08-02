using System;
using System.Collections.Generic;
using System.Numerics;
using DeepDungeon.Fsd.Dalamud.GameState;
using DeepDungeon.Fsd.Dalamud.Map;
using FFXIVClientStructs.FFXIV.Client.Game.InstanceContent;

namespace DeepDungeon.Fsd.Dalamud.Runtime.Navigation
{
	/// <summary>
	/// Provides progressive, room-aware fallback navigation. Attempts the final destination first and,
	/// on repeated failures, falls back to intermediate rooms along the graph path until VNav succeeds.
	/// </summary>
	public sealed class ProgressiveRoomNavigator
	{
		private readonly List<int> _route = new();

		private int? _targetRoom;
		private Vector3 _finalDestination;
		private Vector3 _activeDestination;
		private bool _routeActive;
		private int _activeRouteIndex;
		private string _stageLabel = string.Empty;
		private int _stageRoom = -1;

		public bool IsActive => _routeActive;
		public bool IsStaging => _routeActive && _stageRoom >= 0 && _targetRoom.HasValue && _stageRoom != _targetRoom.Value;
		public string StageLabel => _stageLabel;
		public Vector3 CurrentDestination => _routeActive ? _activeDestination : _finalDestination;

		public void Reset()
		{
			_route.Clear();
			_targetRoom = null;
			_routeActive = false;
			_activeRouteIndex = 0;
			_stageLabel = string.Empty;
			_stageRoom = -1;
			_activeDestination = Vector3.Zero;
			_finalDestination = Vector3.Zero;
		}

		public unsafe void Configure(InstanceContentDeepDungeon* dd, int playerRoom, int? targetRoom, Vector3 finalDestination)
		{
			_finalDestination = finalDestination;

			if (dd == null || !targetRoom.HasValue || playerRoom < 0 || targetRoom.Value < 0)
			{
				Reset();
				_activeDestination = finalDestination;
				return;
			}

			if (!RoomGraph.TryBuildRoomRoute(dd, playerRoom, targetRoom.Value, _route) || _route.Count <= 2)
			{
				Reset();
				_targetRoom = targetRoom;
				_activeDestination = finalDestination;
				return;
			}

			_targetRoom = targetRoom.Value;
			_routeActive = true;
			_activeRouteIndex = _route.Count - 1;
			_stageRoom = _targetRoom.Value;
			_activeDestination = finalDestination;
			UpdateStageDestination(dd);
		}

		public unsafe bool TryHandleArrival(InstanceContentDeepDungeon* dd, int playerRoom, out Vector3 nextDestination)
		{
			nextDestination = _finalDestination;

			if (!_routeActive || !_targetRoom.HasValue)
			{
				return true;
			}

			if (playerRoom == _targetRoom.Value)
			{
				Reset();
				nextDestination = _finalDestination;
				return true;
			}

			if (!RoomGraph.TryBuildRoomRoute(dd, playerRoom, _targetRoom.Value, _route) || _route.Count <= 2)
			{
				_routeActive = false;
				_stageLabel = string.Empty;
				nextDestination = _finalDestination;
				return false;
			}

			_activeRouteIndex = _route.Count - 1;
			if (!UpdateStageDestination(dd))
			{
				_routeActive = false;
				nextDestination = _finalDestination;
				return false;
			}

			nextDestination = _activeDestination;
			return false;
		}

		public unsafe bool TryHandleFailure(InstanceContentDeepDungeon* dd, int playerRoom, out Vector3 fallbackDestination)
		{
			fallbackDestination = _finalDestination;

			if (!_routeActive || !_targetRoom.HasValue)
				return false;

			int playerRouteIndex = FindRoomIndex(playerRoom);
			if (playerRouteIndex < 0)
			{
				if (!RoomGraph.TryBuildRoomRoute(dd, playerRoom, _targetRoom.Value, _route))
				{
					_routeActive = false;
					return false;
				}

				playerRouteIndex = 0;
				_activeRouteIndex = _route.Count - 1;
			}

			int gap = _activeRouteIndex - playerRouteIndex;
			if (gap <= 1)
			{
				return false;
			}

			int step = Math.Max(1, gap / 2);
			_activeRouteIndex = Math.Max(playerRouteIndex + 1, _activeRouteIndex - step);

			if (!UpdateStageDestination(dd))
			{
				_routeActive = false;
				return false;
			}

			fallbackDestination = _activeDestination;
			return true;
		}

		private unsafe bool UpdateStageDestination(InstanceContentDeepDungeon* dd)
		{
			if (!_routeActive)
				return false;

			if (_activeRouteIndex >= _route.Count - 1 || dd == null)
			{
				_stageRoom = _targetRoom ?? -1;
				_activeDestination = _finalDestination;
				_stageLabel = _targetRoom.HasValue ? $"final room {_targetRoom.Value}" : "final destination";
				return true;
			}

			int room = _route[_activeRouteIndex];
			if (!TryResolveRoomCenter(dd, room, out var dest))
			{
				return false;
			}

			int hopsRemaining = (_route.Count - 1) - _activeRouteIndex;
			_stageRoom = room;
			_activeDestination = dest;
			_stageLabel = $"staging via room {room} (remaining {hopsRemaining})";
			return true;
		}

		private static unsafe bool TryResolveRoomCenter(InstanceContentDeepDungeon* dd, int room, out Vector3 dest)
		{
			dest = Vector3.Zero;

			if (dd == null || room < 0)
				return false;

			if (MapPos.TryGetRoomCenter(dd, room, out dest))
			{
				return true;
			}

			return false;
		}

		private int FindRoomIndex(int room)
		{
			if (room < 0)
				return -1;
			return _route.IndexOf(room);
		}
	}
}
