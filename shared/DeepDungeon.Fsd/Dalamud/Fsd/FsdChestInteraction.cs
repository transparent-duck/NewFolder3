using System;
using System.Collections.Generic;
using System.Numerics;
using global::Dalamud.Game.ClientState.Conditions;
using global::Dalamud.Game.ClientState.Objects.Types;
using DeepDungeon.Fsd.Core;
using DeepDungeon.Fsd.Dalamud.Runtime;
using DeepDungeon.Fsd.Dalamud.Runtime.Floor;
using DeepDungeon.Fsd.Dalamud.Runtime.Search;
using DeepDungeon.Fsd.Dalamud;
using DeepDungeon.Fsd.Runtime;

namespace DeepDungeon.Fsd.Dalamud
{
	internal sealed class ChestInteractionAttempt
	{
		internal ChestInteractionAttempt(RoomWaypoint waypoint)
		{
			Waypoint = waypoint;
		}

		internal RoomWaypoint Waypoint { get; }
		internal uint EntityId;
		internal DateTime InteractionStartedAtUtc;
		internal long EvidenceSequenceAtStart;
		internal DateTime NextInteractAt;
		internal bool Reapproaching;
		internal bool AcceptanceRecorded;
		internal uint? PendingGoldOvercapSlotIndex;
	}

	internal readonly record struct ChestLifecycleSnapshot(
		uint ExpectedEntityId,
		DateTime InteractionStartedAtUtc,
		long EvidenceSequenceAtStart,
		long EvidenceSequence,
		FloorChestEvidence Evidence,
		NativeTreasureCompletionDecision Decision,
		bool AcceptanceRecorded);

    internal class FsdChestInteraction
    {
		private readonly FsdSettings _configuration;
		private readonly IRunOptionsProvider _runOptionsProvider;
		private const double ChestInteractionRetrySeconds = 1.0;
		private const float NormalChestOpenTimeoutSeconds = 30.0f;
		private const float AggressiveChestOpenTimeoutSeconds = 10.0f;
		private const float BandedChestOpenTimeoutSeconds = 120.0f;
		private const float SilverHpThreshold = 0.85f;

        // Known Deep Dungeon treasure object DataIds
        private const uint SilverCoffer = 0x1EA13D;
        private const uint GoldCoffer = 0x1EA13E;
        private const uint BandedCoffer = 0x1EA1F7;

        // Bronze variants by territory (from BossMod)
        private static readonly HashSet<uint> BronzeChestIDs = new()
        {
            // PotD
            782, 783, 784, 785, 786, 787, 788, 789, 790, 802, 803, 804, 805,
            // HoH
            1036, 1037, 1038, 1039, 1040, 1041, 1042, 1043, 1044, 1045, 1046, 1047, 1048, 1049,
            // EO
            1541, 1542, 1543, 1544, 1545, 1546, 1547, 1548, 1549, 1550, 1551, 1552, 1553, 1554,
            // PT
            1881, 1882, 1883, 1884, 1885, 1886, 1887, 1888, 1889, 1890, 1891, 1892, 1893, 1906, 1907, 1908
        };

        public FsdChestInteraction(FsdSettings configuration, IRunOptionsProvider runOptionsProvider)
        {
            _configuration = configuration;
            _runOptionsProvider = runOptionsProvider;
        }

		internal static bool HasInteraction(ChestInteractionAttempt? attempt) => attempt?.EntityId != 0;

		internal bool CanStart(ChestInteractionAttempt? attempt, RoomWaypoint waypoint)
		{
			if (attempt?.Waypoint != waypoint)
				return false;
			if (waypoint.Type != RoomObjectiveType.ChestSilver)
				return true;

			var player = Service.LocalPlayer;
			return player != null &&
			       (float)player.CurrentHp / Math.Max(1u, player.MaxHp) >= SilverHpThreshold;
		}

		internal bool IsTargetMissing(ChestInteractionAttempt? attempt, FloorObjectEvidenceSnapshot evidence)
		{
			return attempt != null && evidence.Available &&
			       !TryFindWaypointChestEvidence(evidence, attempt.Waypoint, 0, out _);
		}

		internal bool IsAccepted(
			ChestInteractionAttempt? attempt,
			RoomWaypoint waypoint,
			FloorObjectEvidenceSnapshot? evidence,
			out ChestLifecycleSnapshot snapshot,
			out bool newlyAccepted)
		{
			if (attempt == null || attempt.Waypoint != waypoint)
			{
				snapshot = default;
				newlyAccepted = false;
				return false;
			}

			snapshot = Observe(attempt, evidence);
			newlyAccepted = snapshot.Decision.Complete && !attempt.AcceptanceRecorded;
			if (newlyAccepted)
			{
				attempt.AcceptanceRecorded = true;
				snapshot = snapshot with { AcceptanceRecorded = true };
			}
			return snapshot.Decision.Complete;
		}

		internal ChestLifecycleSnapshot Observe(ChestInteractionAttempt? attempt, FloorObjectEvidenceSnapshot? evidence)
		{
			if (attempt == null)
				return default;

			FloorChestEvidence chest = default;
			long evidenceSequence = evidence?.RefreshSequence ?? 0;
			bool present = evidence?.Available == true &&
			               TryFindWaypointChestEvidence(evidence, attempt.Waypoint, attempt.EntityId, out chest);
			var decision = NativeTreasureCompletionPlanner.Decide(new NativeTreasureCompletionSnapshot(
				attempt.EntityId != 0,
				attempt.EntityId,
				attempt.EvidenceSequenceAtStart,
				present,
				present ? chest.Object.EntityId : 0,
				evidenceSequence,
				present && chest.NativeStateAvailable,
				present ? chest.NativeCompletionKind : NativeTreasureCompletionKind.Unavailable,
				present && chest.Object.IsTargetable,
				present ? (byte)chest.State : (byte)0,
				present ? (byte)chest.Flags : (byte)0,
				present,
				attempt.InteractionStartedAtUtc == DateTime.MinValue
					? 0
					: (DateTime.UtcNow - attempt.InteractionStartedAtUtc).TotalSeconds,
				ChestInteractionRetrySeconds));

			return new ChestLifecycleSnapshot(
				attempt.EntityId,
				attempt.InteractionStartedAtUtc,
				attempt.EvidenceSequenceAtStart,
				evidenceSequence,
				chest,
				decision,
				attempt.AcceptanceRecorded);
		}

		internal bool TryInteract(
			ChestInteractionAttempt? attempt,
			FloorObjectEvidenceSnapshot evidence,
			out ChestLifecycleSnapshot snapshot,
			out bool retry)
        {
			snapshot = default;
			retry = false;
            try
			{
				if (attempt == null)
					return false;
				if (attempt.Reapproaching)
					return false;
				bool aggressiveInteraction = _configuration.AggressiveChestInteraction;
				snapshot = Observe(attempt, evidence);
				if (snapshot.Decision.Complete ||
				    !aggressiveInteraction && attempt.EntityId != 0 && !snapshot.Decision.RetryInteraction)
				{
					return false;
				}
				// Only active during auto-farming: require run options provider assigned by engine
				if (!DeepDungeonHelper.IsInDeepDungeon())
                    return false;

				long optionsVersion;
				RunOptions opts;
				do
				{
					optionsVersion = _runOptionsProvider.Version;
					opts = _runOptionsProvider.Current;
				}
				while (optionsVersion != _runOptionsProvider.Version);
				if (!opts.OpenGold && !opts.OpenSilver && !opts.OpenBronze && !opts.BandedEnabled)
                    return false;
				if (!evidence.Available)
					return false;

				if (!CanAttemptInteraction(aggressiveInteraction))
					return false;

                var player = Service.LocalPlayer;
                if (player == null || player.IsDead)
                    return false;

				// Resolve only the chest owned by the active waypoint.
				var maxDist = GetInteractionDistance();
				if (!TryFindWaypointChestEvidence(evidence, attempt.Waypoint, attempt.EntityId, out var chestEvidence) ||
				    attempt.EntityId != 0 && chestEvidence.Object.EntityId != attempt.EntityId ||
				    !chestEvidence.Object.IsTargetable ||
				    !IsAllowedChest(chestEvidence.Kind, opts))
					return false;
				if (!aggressiveInteraction && DateTime.UtcNow < attempt.NextInteractAt)
					return false;
				var best = ResolveCurrentObject(chestEvidence);
				if (best == null || !best.IsTargetable || !IsAllowedChest(best, opts))
					return false;

				var dx = best.Position.X - player.Position.X;
				var dz = best.Position.Z - player.Position.Z;
				if (dx * dx + dz * dz > maxDist * maxDist)
					return false;

				// silver explosion safety
				if (!CanStart(attempt, attempt.Waypoint))
					return false;

				bool wasRetry = attempt.EntityId != 0;
				var interactionStartedAtUtc = DateTime.UtcNow;
				attempt.NextInteractAt = interactionStartedAtUtc.AddSeconds(ChestInteractionRetrySeconds);
				var interacted = GameInteraction.InteractWith(best, maxDist, force: IsBanded(best));
				if (!interacted)
					return false;

				attempt.EntityId = best.EntityId;
				attempt.InteractionStartedAtUtc = interactionStartedAtUtc;
				attempt.EvidenceSequenceAtStart = evidence.RefreshSequence;
				attempt.AcceptanceRecorded = false;
				retry = wasRetry;
				snapshot = Observe(attempt, evidence);
				return true;
            }
            catch (Exception ex)
            {
				Service.Log.Error($"[NecromancerChest] Interaction error: {ex}");
				return false;
            }
        }

		private static bool TryFindWaypointChestEvidence(
			FloorObjectEvidenceSnapshot evidence,
			RoomWaypoint waypoint,
			uint expectedEntityId,
			out FloorChestEvidence chest)
		{
			for (int i = 0; i < evidence.Chests.Count; i++)
			{
				chest = evidence.Chests[i];
				if (Vector3.Distance(chest.Object.Position, waypoint.Position) >= 0.5f)
					continue;

				bool typeMatches = waypoint.Type switch
				{
					RoomObjectiveType.ChestBanded => chest.Kind == FloorChestKind.Banded,
					RoomObjectiveType.ChestGold => chest.Kind == FloorChestKind.Gold,
					RoomObjectiveType.ChestSilver => chest.Kind == FloorChestKind.Silver,
					RoomObjectiveType.ChestBronze => chest.Kind == FloorChestKind.Bronze,
					_ => false
				};
				if (!typeMatches)
					continue;
				if (expectedEntityId == 0 || chest.Object.EntityId == expectedEntityId)
					return true;
			}

			chest = default;
			return false;
		}

		internal bool TryBeginOrContinueReapproach(
			ChestInteractionAttempt? attempt,
			Vector3 playerPosition,
			out bool started)
		{
			started = false;
			if (attempt == null)
				return false;

			if (attempt.Reapproaching)
				return true;

			float maxDist = GetInteractionDistance();
			float dx = attempt.Waypoint.Position.X - playerPosition.X;
			float dz = attempt.Waypoint.Position.Z - playerPosition.Z;
			if (dx * dx + dz * dz <= maxDist * maxDist)
				return false;

			attempt.Reapproaching = true;
			started = true;
			return true;
		}

		internal void FinishReapproach(ChestInteractionAttempt attempt)
		{
			attempt.Reapproaching = false;
			attempt.NextInteractAt = DateTime.MinValue;
		}

		internal float GetOpenTimeoutSeconds(RoomWaypoint waypoint)
		{
			if (waypoint.Type == RoomObjectiveType.ChestBanded)
				return BandedChestOpenTimeoutSeconds;

			return _configuration.AggressiveChestInteraction
				? AggressiveChestOpenTimeoutSeconds
				: NormalChestOpenTimeoutSeconds;
		}

		private float GetInteractionDistance()
		{
			return Math.Clamp(_configuration.NecromancerChestInteractDistance, 0.5f, 6.0f);
		}

		private static IGameObject? ResolveCurrentObject(FloorChestEvidence evidence)
		{
			var obj = Service.GameObjects[evidence.Object.ObjectIndex];
			if (obj == null ||
			    obj.GameObjectId != evidence.Object.GameObjectId ||
			    obj.EntityId != evidence.Object.EntityId ||
			    obj.BaseId != evidence.Object.BaseId ||
			    !TryClassify(obj, out var kind) ||
			    kind != evidence.Kind)
			{
				return null;
			}
			return obj;
		}

		internal static bool IsGold(IGameObject obj) => obj.BaseId == GoldCoffer;
		internal static bool IsSilver(IGameObject obj) => obj.BaseId == SilverCoffer;
		internal static bool IsBanded(IGameObject obj) => obj.BaseId == BandedCoffer;
		internal static bool IsBronze(IGameObject obj) => BronzeChestIDs.Contains(obj.BaseId);
		internal static bool TryClassify(IGameObject obj, out FloorChestKind kind)
		{
			if (IsBanded(obj))
			{
				kind = FloorChestKind.Banded;
				return true;
			}
			if (IsGold(obj))
			{
				kind = FloorChestKind.Gold;
				return true;
			}
			if (IsSilver(obj))
			{
				kind = FloorChestKind.Silver;
				return true;
			}
			if (IsBronze(obj))
			{
				kind = FloorChestKind.Bronze;
				return true;
			}

			kind = default;
			return false;
		}
		private bool IsAllowedChest(IGameObject obj, RunOptions opts)
        {
			// Banded: controlled exclusively by bandedEnabled
			if (IsBanded(obj)) return opts.BandedEnabled;
			// Gold/Silver/Bronze
			if (opts.OpenGold && IsGold(obj)) return true;
			if (opts.OpenSilver && IsSilver(obj)) return true;
			if (opts.OpenBronze && IsBronze(obj)) return true;
			return false;
        }

		private static bool IsAllowedChest(FloorChestKind kind, RunOptions opts)
		{
			return kind switch
			{
				FloorChestKind.Banded => opts.BandedEnabled,
				FloorChestKind.Gold => opts.OpenGold,
				FloorChestKind.Silver => opts.OpenSilver,
				FloorChestKind.Bronze => opts.OpenBronze,
				_ => false
			};
		}

		private static bool CanAttemptInteraction(bool aggressiveInteraction)
		{
			return (aggressiveInteraction || !Service.Condition[ConditionFlag.Casting]) &&
			       !Service.Condition[ConditionFlag.BetweenAreas] &&
			       !Service.Condition[ConditionFlag.BetweenAreas51];
		}
    }
}


