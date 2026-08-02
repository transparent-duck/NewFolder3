using System;
using global::Dalamud.Game.ClientState.Objects.SubKinds;
using DeepDungeon.Fsd.Core;
using DeepDungeon.Fsd.Dalamud.GameState;
using DeepDungeon.Fsd.Dalamud.Runtime.Navigation;
using FFXIVClientStructs.FFXIV.Client.Game.InstanceContent;

namespace DeepDungeon.Fsd.Dalamud.Runtime.Floor
{
	public sealed partial class FloorPhaseController
	{
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
			int? targetRoom = (!usedActor && passageRoomIndex >= 0) ? passageRoomIndex : null;

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
