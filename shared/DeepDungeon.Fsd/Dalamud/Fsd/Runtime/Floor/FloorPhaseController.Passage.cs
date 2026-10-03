using System;
using System.Numerics;
using global::Dalamud.Plugin.Ipc;
using global::Dalamud.Game.ClientState.Objects.SubKinds;
using DeepDungeon.Fsd.Core;
using DeepDungeon.Fsd.Dalamud.GameState;
using DeepDungeon.Fsd.Dalamud.Runtime.Navigation;
using FFXIVClientStructs.FFXIV.Client.Game.InstanceContent;

namespace DeepDungeon.Fsd.Dalamud.Runtime.Floor
{
	public sealed partial class FloorPhaseController
	{
		private ICallGateSubscriber<Vector3, float, float, Vector3?>? _passageMeshPoint;
		private Vector3 _passageActorPosition, _passageWalkPosition;
		private long _passageProjectionGeneration = -1;
		private DateTime _nextPassageProjectionAt;
		private bool _passageProjectionAccepted;

		private Vector3 ResolvePassageWalkingPosition(Vector3 actor)
		{
			long generation = _floorRuntime?.Generation ?? -1;
			if (generation != _passageProjectionGeneration || Vector3.DistanceSquared(actor, _passageActorPosition) > 0.01f)
			{
				_passageProjectionGeneration = generation;
				_passageActorPosition = _passageWalkPosition = actor;
				_passageProjectionAccepted = false;
				_nextPassageProjectionAt = DateTime.MinValue;
			}
			if (_passageProjectionAccepted || DateTime.UtcNow < _nextPassageProjectionAt) return _passageWalkPosition;
			_nextPassageProjectionAt = DateTime.UtcNow.AddSeconds(1);
			try
			{
				_passageMeshPoint ??= Service.PluginInterface.GetIpcSubscriber<Vector3, float, float, Vector3?>("vnavmesh.Query.Mesh.NearestPoint");
				if (_passageMeshPoint.HasFunction && PassageNavigationPolicy.TryProjectActor(actor,
					_passageMeshPoint.InvokeFunc(actor, 0.25f, 4f), out var projected))
				{
					_passageWalkPosition = projected;
					_passageProjectionAccepted = true;
					Service.Log.Info($"[FloorPhase] Passage walking destination: actor={actor}, mesh={projected}, generation={generation}");
				}
			}
			catch { /* Keep the actor destination if optional projection is unavailable. */ }
			return _passageWalkPosition;
		}

		private unsafe void UpdatePassageNavigation(InstanceContentDeepDungeon* dd)
		{
			var player = Service.LocalPlayer;
			if (player == null) return;

			_chaseHelper.Reset();
			ResetPatrolPlan();
			_ctx?.ClearPreferredAggroTarget();
			if (!RequireTransitionPermission("passage navigation", FloorObjectiveKind.EnterPassage))
				return;
			NavigateToPassage(dd, player);
		}

		private unsafe void NavigateToPassage(InstanceContentDeepDungeon* dd, IPlayerCharacter player)
		{
			var objectEvidence = _floorRuntime?.ObjectEvidence.Current;
			if (objectEvidence?.Available != true ||
			    !PassageLocator.TryResolvePassageDestination(dd, objectEvidence, out var dest, out bool usedActor, out var passageRoomIndex))
			{
				_status = "Passage position unknown";
				RecordPassageNavigationEvent("passage-navigation", "Unknown", usedActor: false, passageRoomIndex: -1, playerRoom: RoomGraph.GetLocalPlayerRoomIndex(dd));
				_navDriver!.Reset();
				return;
			}

			int playerRoom = RoomGraph.GetLocalPlayerRoomIndex(dd);
			// Knowing the actor does not make a failing cross-room path reachable.
			// Keep the room graph fallback until inside its room, then approach the exact walking point.
			int? targetRoom = PassageNavigationPolicy.RoutingRoom(usedActor, passageRoomIndex, playerRoom);
			if (usedActor) dest = ResolvePassageWalkingPosition(dest);

			var result = _navDriver!.Drive(dest, player.Position, 0.5f, dd, playerRoom, targetRoom);

			switch (result)
			{
				case NavDriveResult.Moving:
					RecordPassageNavigationEvent("passage-navigation", result.ToString(), usedActor, passageRoomIndex, playerRoom);
					_status = _navDriver.IsStaging
						? $"Navigating to passage ({_navDriver.StageLabel})"
						: "Navigating to passage...";
					break;
				case NavDriveResult.Staging:
					RecordPassageNavigationEvent("passage-navigation", result.ToString(), usedActor, passageRoomIndex, playerRoom);
					_status = $"Navigating to passage ({_navDriver.StageLabel})";
					break;
				case NavDriveResult.Arrived:
					if (!usedActor)
					{
						_status = "At passage room, waiting for passage actor";
						RecordPassageNavigationEvent("passage-room-center-arrived", result.ToString(), usedActor, passageRoomIndex, playerRoom);
						_navDriver.Reset();
						break;
					}
					_floorRuntime?.RunTelemetry?.ObservePassageCommit(DateTime.UtcNow);
					bool newlyPositioned = _status != "At passage, waiting for transition";
					_status = "At passage, waiting for transition";
					if (newlyPositioned)
					{
						Service.Log.Info("[FloorPhase] Positioned at passage, waiting for transition");
						RecordPassageNavigationEvent("passage-arrived", result.ToString(), usedActor, passageRoomIndex, playerRoom);
					}
					break;
				case NavDriveResult.StuckRetrying:
					_floorRuntime?.RunTelemetry?.ObserveNavigationIssue();
					RecordPassageNavigationEvent("passage-navigation", result.ToString(), usedActor, passageRoomIndex, playerRoom);
					_status = $"Stuck, repathing ({_navDriver.StuckRetryCount}/3)";
					break;
				case NavDriveResult.Failed:
					_floorRuntime?.RunTelemetry?.ObserveNavigationIssue();
					RecordPassageNavigationEvent("passage-navigation", result.ToString(), usedActor, passageRoomIndex, playerRoom);
					_status = "Passage navigation failed";
					break;
			}
		}
	}
}
