using System;
using System.Numerics;
using DeepDungeon.Fsd.Core;
using global::Dalamud.Game.ClientState.Objects.Types;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.InstanceContent;
using OmenTools.Interop.Game;

namespace DeepDungeon.Fsd.Dalamud.Runtime.Floor
{
	internal unsafe sealed class Pt30DivineFavorFlashHelper : IDisposable
	{
		private const uint PilgrimsTraverseDungeonId = 4;
		private const byte Pt30Floor = 30;
		private const uint DivineFavorVisualActionId = 44917;
		private const uint DivineFavorFirstActionId = 44918;

		private static readonly TimeSpan DivineFavorVisualOrbitDuration = TimeSpan.FromSeconds(13.5);
		private static readonly TimeSpan DivineFavorFirstOrbitDuration = TimeSpan.FromSeconds(9.0);
		private static readonly TimeSpan OrbitFailureLogInterval = TimeSpan.FromSeconds(1);
		private static readonly TimeSpan OrbitStallSampleInterval = TimeSpan.FromSeconds(0.55);
		private static readonly TimeSpan OrbitDirectionReverseCooldown = TimeSpan.FromSeconds(0.75);
		private const float OrbitStallDistanceSquared = 0.16f;

		private readonly MovementInputController _movementInputController = new();
		private DateTime _divineFavorOrbitUntil = DateTime.MinValue;
		private DateTime _lastOrbitFailureLogAt = DateTime.MinValue;
		private DateTime _lastOrbitSampleAt = DateTime.MinValue;
		private DateTime _lastOrbitDirectionReverseAt = DateTime.MinValue;
		private Vector3 _lastOrbitSamplePosition;
		private int _orbitDirection;
		private bool _disposed;

		public bool IsDivineFavorMovementActive => IsDivineFavorOrbitActive(DateTime.UtcNow) && _movementInputController.Enabled;

		public Pt30DivineFavorFlashHelper()
		{
			_movementInputController.IsAutoMove = false;
			if (_movementInputController.Enabled)
				_movementInputController.Enabled = false;
		}

		public void Dispose()
		{
			if (_disposed)
				return;

			StopOrbit();
			_movementInputController.Dispose();
			_disposed = true;
		}

		public void Reset()
		{
			StopOrbit();
		}

		public void Update(InstanceContentDeepDungeon* dd)
		{
			if (!IsPilgrimsTraverseFloor30(dd))
			{
				StopOrbit();
				return;
			}

			var now = DateTime.UtcNow;
			var player = Service.LocalPlayer;
			if (player == null || player.IsDead)
			{
				StopOrbit();
				return;
			}

			TryEnterOrbitFromActiveCast(player.Position, now);

			if (IsDivineFavorOrbitActive(now))
			{
				UpdateOrbitMovement(player.Position, now);
				return;
			}

			StopOrbit();
		}

		public bool TryUpdateBossEngageMovement(InstanceContentDeepDungeon* dd, Vector3 playerPosition, out string status)
		{
			status = string.Empty;
			if (!IsPilgrimsTraverseFloor30(dd) || IsDivineFavorOrbitActive(DateTime.UtcNow))
				return false;

			if (!TryFindBossEngageDestination(playerPosition, out var destination))
			{
				StopMovement();
				status = "Boss floor - PT30 engage point unavailable";
				return true;
			}

			if (Vector3.DistanceSquared(playerPosition, destination) <= Pt30BossGeometry.BossEngageArrivalRadius * Pt30BossGeometry.BossEngageArrivalRadius)
			{
				StopMovement();
				status = "Boss floor - waiting for PT30 boss aggro";
				return true;
			}

			_movementInputController.DesiredPosition = destination;
			_movementInputController.IsAutoMove = false;
			if (!_movementInputController.Enabled)
				_movementInputController.Enabled = true;
			status = "Boss floor - approaching PT30 boss trigger";
			return true;
		}

		private bool IsDivineFavorOrbitActive(DateTime now)
		{
			return _divineFavorOrbitUntil != DateTime.MinValue && now < _divineFavorOrbitUntil;
		}

		private void TryEnterOrbitFromActiveCast(Vector3 playerPosition, DateTime now)
		{
			if (IsDivineFavorOrbitActive(now))
				return;

			foreach (var obj in Service.GameObjects)
			{
				if (obj is not IBattleChara battleChara)
					continue;
				if (!battleChara.IsCasting)
					continue;
				if (battleChara.CastActionType != (byte)ActionType.Action)
					continue;
				var actionId = battleChara.CastActionId;
				if (actionId != DivineFavorVisualActionId && actionId != DivineFavorFirstActionId)
					continue;

				var duration = actionId == DivineFavorVisualActionId
					? DivineFavorVisualOrbitDuration
					: DivineFavorFirstOrbitDuration;
				_divineFavorOrbitUntil = now.Add(duration);
				_lastOrbitFailureLogAt = DateTime.MinValue;
				_orbitDirection = Pt30BossGeometry.ChooseOrbitDirection(new Vector2(playerPosition.X, playerPosition.Z));
				ResetOrbitSampling(playerPosition, now);
				_movementInputController.IsAutoMove = true;
				_movementInputController.Enabled = true;
				Service.Log.Info($"[FloorPhase] PT30 Divine Favor cast {actionId} detected from {battleChara.Name} -> entering orbit mode");
				return;
			}
		}

		private void UpdateOrbitMovement(Vector3 playerPosition, DateTime now)
		{
			UpdateOrbitProgress(playerPosition, now);

			if (!TryFindOrbitDestination(playerPosition, out var destination))
			{
				_movementInputController.Enabled = false;
				if (now - _lastOrbitFailureLogAt >= OrbitFailureLogInterval)
				{
					_lastOrbitFailureLogAt = now;
					Service.Log.Debug($"[FloorPhase] PT30 Divine Favor orbit has no arena-safe destination from {playerPosition}");
				}
				return;
			}

			_movementInputController.DesiredPosition = destination;
			_movementInputController.IsAutoMove = true;
			if (!_movementInputController.Enabled)
				_movementInputController.Enabled = true;
		}

		private bool TryFindOrbitDestination(Vector3 playerPosition, out Vector3 destination)
		{
			var player2 = new Vector2(playerPosition.X, playerPosition.Z);
			if (_orbitDirection == 0)
				_orbitDirection = Pt30BossGeometry.ChooseOrbitDirection(player2);

			if (!Pt30BossGeometry.TryFindOrbitDestination(player2, _orbitDirection, out var destination2))
			{
				destination = default;
				return false;
			}

			destination = new Vector3(destination2.X, playerPosition.Y, destination2.Y);
			return true;
		}

		private static bool TryFindBossEngageDestination(Vector3 playerPosition, out Vector3 destination)
		{
			var player2 = new Vector2(playerPosition.X, playerPosition.Z);
			if (!Pt30BossGeometry.TryFindBossEngageDestination(player2, out var destination2))
			{
				destination = default;
				return false;
			}

			destination = new Vector3(destination2.X, playerPosition.Y, destination2.Y);
			return true;
		}

		private void StopOrbit()
		{
			_divineFavorOrbitUntil = DateTime.MinValue;
			_lastOrbitFailureLogAt = DateTime.MinValue;
			_lastOrbitSampleAt = DateTime.MinValue;
			_lastOrbitDirectionReverseAt = DateTime.MinValue;
			_orbitDirection = 0;
			StopMovement();
		}

		private void StopMovement()
		{
			if (_disposed)
				return;

			if (_movementInputController.Enabled)
				_movementInputController.Enabled = false;
			_movementInputController.IsAutoMove = false;
		}

		private static bool IsPilgrimsTraverseFloor30(InstanceContentDeepDungeon* dd)
		{
			return dd != null && dd->DeepDungeonId == PilgrimsTraverseDungeonId && dd->Floor == Pt30Floor;
		}

		private void ResetOrbitSampling(Vector3 playerPosition, DateTime now)
		{
			_lastOrbitSamplePosition = playerPosition;
			_lastOrbitSampleAt = now;
			_lastOrbitDirectionReverseAt = DateTime.MinValue;
		}

		private void UpdateOrbitProgress(Vector3 playerPosition, DateTime now)
		{
			if (_lastOrbitSampleAt == DateTime.MinValue)
			{
				ResetOrbitSampling(playerPosition, now);
				return;
			}

			if (now - _lastOrbitSampleAt < OrbitStallSampleInterval)
				return;

			float movedSquared = Vector3.DistanceSquared(playerPosition, _lastOrbitSamplePosition);
			if (movedSquared < OrbitStallDistanceSquared && now - _lastOrbitDirectionReverseAt >= OrbitDirectionReverseCooldown)
			{
				_orbitDirection = -(_orbitDirection == 0
					? Pt30BossGeometry.ChooseOrbitDirection(new Vector2(playerPosition.X, playerPosition.Z))
					: _orbitDirection);
				_lastOrbitDirectionReverseAt = now;
				Service.Log.Info($"[FloorPhase] PT30 Divine Favor movement stalled ({MathF.Sqrt(movedSquared):F2}m); reversing orbit direction");
			}

			_lastOrbitSamplePosition = playerPosition;
			_lastOrbitSampleAt = now;
		}

	}
}
