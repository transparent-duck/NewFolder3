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
	internal readonly record struct ChestInteractionTarget(Vector3 Position, FloorChestKind Kind);

	internal sealed class ChestInteractionAttempt
	{
		internal ChestInteractionAttempt(ChestInteractionTarget waypoint)
		{
			Waypoint = waypoint;
		}

		internal ChestInteractionTarget Waypoint { get; }
		internal uint EntityId;
		internal DateTime InteractionStartedAtUtc;
		internal long EvidenceSequenceAtStart;
		internal DateTime NextInteractAt;
        internal DateTime NextRejectionDiagnosticAt;
        internal DateTime NextNativeAttemptDiagnosticAt;
		internal bool Reapproaching;
		internal bool AcceptanceRecorded;
		internal uint? PendingGoldOvercapSlotIndex;
		internal uint? PendingSilverOvercapStoneRowId;
		// Bounded dispatch telemetry: every boundary call counts; only the
		// throttled logs sample the expensive player-state description.
		internal int InteractionCallCount;
		internal int NativeDispatchCount;
		internal ulong? LastNativeReturn;
		internal DateTime NextWaitDiagnosticAt;
		internal string? LastWaitDiagnosticReason;
		// Exact reason of the most recent rejection — cleared on each
		// TryInteract invocation, set BEFORE the logging throttle so an
		// always-throttled rejection still surfaces.
		internal string? LastRejectionReason;
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

        // Test seam: environment delegates default to the real game. Tests inject
        // deterministic implementations instead of faking Dalamud objects.
        internal sealed class Env
        {
            internal Func<DateTime> Now = () => DateTime.UtcNow;
            internal Func<bool> InDeepDungeon = DeepDungeonHelper.IsInDeepDungeon;
            internal Func<bool> Casting = () => Service.Condition[ConditionFlag.Casting];
            internal Func<bool> Zoning = () => Service.Condition[ConditionFlag.BetweenAreas]
                || Service.Condition[ConditionFlag.BetweenAreas51];
            // (position, dead, hpFraction, present); absent player => present=false.
            internal Func<(Vector3 pos, bool dead, float hpFrac, bool present)> PlayerState = () =>
            {
                var p = Service.LocalPlayer;
                return p == null ? (default, false, 0f, false)
                    : (p.Position, p.IsDead, (float)p.CurrentHp / Math.Max(1u, p.MaxHp), true);
            };
            // Live target still matches the recorded evidence and is targetable.
            internal Func<FloorChestEvidence, bool> LiveTargetMatches =
                e => ResolveCurrentObject(e) is { IsTargetable: true };
            internal Action<string> Log = m => Service.Log.Info(m);
            internal Action<string> LogError = m => Service.Log.Error(m);
            // Throttled diagnostics only — observation, never an
            // eligibility predicate. Injected in offline probes.
            internal Func<string> InteractionState = GameInteraction.DescribePlayerInteractionState;
            internal Func<FloorChestEvidence, bool?> LiveTargetable =
                e => ResolveCurrentObject(e)?.IsTargetable;
            // Dispatch against evidence; default resolves the live object and calls GameInteraction.
            internal GameInteractionTrace LastInteractionTrace;
            internal Func<FloorChestEvidence, float, bool, (bool interacted, bool dispatched, string failure)> Interact;
            internal Env()
            {
                Interact = (evidence, maxDist, force) =>
                {
                    var best = ResolveCurrentObject(evidence);
                    if (best == null)
                        return (false, false, "live-target-unavailable");
                    var interacted = GameInteraction.InteractWith(best, maxDist, force, out var failure, out bool dispatched, out var trace);
                    LastInteractionTrace = trace;
                    return (interacted, dispatched, failure);
                };
            }
        }
        private readonly Env _env;

        public FsdChestInteraction(FsdSettings configuration, IRunOptionsProvider runOptionsProvider, Env? env = null)
        {
            _configuration = configuration;
            _runOptionsProvider = runOptionsProvider;
            _env = env ?? new Env();
        }

		internal static bool HasInteraction(ChestInteractionAttempt? attempt) => attempt?.EntityId != 0;

		internal bool CanStart(ChestInteractionAttempt? attempt, ChestInteractionTarget waypoint)
		{
			if (attempt?.Waypoint != waypoint)
				return false;
			if (waypoint.Kind != FloorChestKind.Silver)
				return true;

			var player = _env.PlayerState();
			return player.present && player.hpFrac >= SilverHpThreshold;
		}

		internal bool IsTargetMissing(ChestInteractionAttempt? attempt, FloorObjectEvidenceSnapshot evidence)
		{
			return attempt != null && evidence.Available &&
			       !TryFindWaypointChestEvidence(evidence, attempt.Waypoint, 0, out _);
		}

		internal bool IsAccepted(
			ChestInteractionAttempt? attempt,
			ChestInteractionTarget waypoint,
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
				var ev = snapshot.Evidence;
				_env.Log($"[NecromancerChest] interaction-completed entity={attempt.EntityId}; " +
					$"calls={attempt.InteractionCallCount}; dispatches={attempt.NativeDispatchCount}; " +
					$"decision={snapshot.Decision.Status}; completionKind={ev.NativeCompletionKind}; " +
					$"evidenceSeqAtStart={snapshot.EvidenceSequenceAtStart}; evidenceSeq={snapshot.EvidenceSequence}; " +
					$"state={ev.State}; flags={ev.Flags}");
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
					: (_env.Now() - attempt.InteractionStartedAtUtc).TotalSeconds,
				InteractionTiming.GetRetryInterval(_configuration.AggressiveChestInteraction).TotalSeconds));

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
				attempt.LastRejectionReason = null;   // fresh invocation
				if (attempt.Reapproaching)
					return WaitForInteraction(attempt, "reapproaching", in snapshot);
				bool aggressiveInteraction = _configuration.AggressiveChestInteraction;
				snapshot = Observe(attempt, evidence);
				if (snapshot.Decision.Complete)
					return WaitForInteraction(attempt, "already-complete", in snapshot);
				if (!aggressiveInteraction && attempt.EntityId != 0 && !snapshot.Decision.RetryInteraction)
					return WaitForInteraction(attempt, "awaiting-completion-decision", in snapshot);
				// Only active during auto-farming: require run options provider assigned by engine
				if (!_env.InDeepDungeon())
                    return RejectInteraction(attempt, "not-in-deep-dungeon");

				long optionsVersion;
				RunOptions opts;
				do
				{
					optionsVersion = _runOptionsProvider.Version;
					opts = _runOptionsProvider.Current;
				}
				while (optionsVersion != _runOptionsProvider.Version);
				if (!opts.OpenGold && !opts.OpenSilver && !opts.OpenBronze && !opts.BandedEnabled)
                    return RejectInteraction(attempt, "no-enabled-targets");
				if (!evidence.Available)
					return RejectInteraction(attempt, "object-evidence-unavailable");

				if (!CanAttemptInteraction(aggressiveInteraction))
					return RejectInteraction(attempt, "casting-or-zoning");

                var player = _env.PlayerState();
                if (!player.present || player.dead)
                    return RejectInteraction(attempt, "player-unavailable-or-dead");

				// Resolve only the chest owned by the active waypoint.
				var maxDist = GetInteractionDistance();
				if (!TryFindWaypointChestEvidence(evidence, attempt.Waypoint, attempt.EntityId, out var chestEvidence))
					return RejectInteraction(attempt, "waypoint-target-missing");
				if (!chestEvidence.Object.IsTargetable)
					return RejectInteraction(attempt, "waypoint-target-not-targetable");
				if (!IsAllowedChest(chestEvidence.Kind, opts))
					return RejectInteraction(attempt, "waypoint-target-disabled");
				if (!aggressiveInteraction && _env.Now() < attempt.NextInteractAt)
					return WaitForInteraction(attempt, "retry-backoff", in snapshot);
				if (!_env.LiveTargetMatches(chestEvidence) || !IsAllowedChest(chestEvidence.Kind, opts))
					return RejectInteraction(attempt, "live-target-unavailable-or-disabled");

				var dx = chestEvidence.Object.Position.X - player.pos.X;
				var dz = chestEvidence.Object.Position.Z - player.pos.Z;
				if (!GetInteractionRange().Contains(player.pos, chestEvidence.Object.Position))
					return RejectInteraction(attempt, "horizontal-range-rejected", chestEvidence);
                if (Runtime.Navigation.CombatPositionNavigation.Sight(player.pos, chestEvidence.Object.Position, out _)
                    == Runtime.Navigation.CombatSight.Blocked)
                    return RejectInteraction(attempt, "line-of-sight-blocked", chestEvidence);

				// silver explosion safety
				if (!CanStart(attempt, attempt.Waypoint))
					return RejectInteraction(attempt, "silver-hp-or-waypoint-guard", chestEvidence);

				bool wasRetry = attempt.EntityId != 0;
				var interactionStartedAtUtc = _env.Now();
				attempt.NextInteractAt = interactionStartedAtUtc.Add(InteractionTiming.GetRetryInterval(aggressiveInteraction));
				var force = chestEvidence.Kind == FloorChestKind.Banded;
				attempt.InteractionCallCount++;
				// One-second sampled logging only — interaction timing is
				// unchanged; unsampled calls never touch the state delegates.
				var diagnosticDue = interactionStartedAtUtc >= attempt.NextNativeAttemptDiagnosticAt;
				if (diagnosticDue)
				{
					attempt.NextNativeAttemptDiagnosticAt = interactionStartedAtUtc.AddSeconds(1);
					var sev = snapshot.Evidence;
					_env.Log($"[NecromancerChest] interaction-call n={attempt.InteractionCallCount}; " +
						$"entity={chestEvidence.Object.EntityId}; gameObjectId={chestEvidence.Object.GameObjectId}; " +
						$"baseId={chestEvidence.Object.BaseId}; objectIndex={chestEvidence.Object.ObjectIndex}; " +
						$"kind={chestEvidence.Kind}; force={force}; " +
						$"distanceXZ={MathF.Sqrt(dx * dx + dz * dz):F2}; " +
						$"deltaY={chestEvidence.Object.Position.Y - player.pos.Y:F2}; maxDistance={maxDist:F2}; " +
						$"evidenceSequence={evidence.RefreshSequence}; " +
						$"decision={snapshot.Decision.Status}; complete={snapshot.Decision.Complete}; " +
						$"completionKind={sev.NativeCompletionKind}; evidenceState={sev.State}; evidenceFlags={sev.Flags}; " +
						$"before={DescribeInteractionState()}");
				}
				_env.LastInteractionTrace = default;
				var (interacted, dispatched, failure) = _env.Interact(chestEvidence, maxDist, force);
				var trace = _env.LastInteractionTrace;
				if (dispatched)
				{
					attempt.NativeDispatchCount++;
					attempt.LastNativeReturn = trace.RawReturn;
				}
				if (diagnosticDue)
				{
					_env.Log($"[NecromancerChest] native-result n={attempt.InteractionCallCount}; " +
						$"dispatches={attempt.NativeDispatchCount}; callEntered={trace.NativeCallEntered}; " +
						$"dispatched={dispatched}; nativeKind={(trace.NativeKind?.ToString() ?? "unknown")}; " +
						$"rangeAllowed={(trace.RangeAllowed?.ToString() ?? "not-checked")}; checkLineOfSightRequested=true; " +
						$"rawReturn={(trace.RawReturn.HasValue ? "0x" + trace.RawReturn.Value.ToString("X") : "unknown")}; " +
						$"nonzero={interacted}; reason={failure}; " +
						$"evidenceTargetable={snapshot.Evidence.Object.IsTargetable}; " +
						$"liveTargetableAfter={DescribeLiveTargetable(chestEvidence)}; " +
						$"after={DescribeInteractionState()}");
				}
				if (!dispatched)
					return RejectInteraction(attempt, failure, chestEvidence);

				attempt.EntityId = chestEvidence.Object.EntityId;
				attempt.InteractionStartedAtUtc = interactionStartedAtUtc;
				attempt.EvidenceSequenceAtStart = evidence.RefreshSequence;
				attempt.AcceptanceRecorded = false;
				retry = wasRetry;
				snapshot = Observe(attempt, evidence);
				return true;
            }
            catch (Exception ex)
            {
				_env.LogError($"[NecromancerChest] Interaction error: {ex}");
				return false;
            }
        }

        private bool RejectInteraction(ChestInteractionAttempt attempt, string reason, FloorChestEvidence? chest = null)
        {
            attempt.LastRejectionReason = reason;   // before the 3s throttle
            var now = _env.Now();
            if (now < attempt.NextRejectionDiagnosticAt) return false;
            attempt.NextRejectionDiagnosticAt = now.AddSeconds(3);
            var player = _env.PlayerState();
            var position = chest?.Object.Position ?? attempt.Waypoint.Position;
            var delta = !player.present ? Vector3.Zero : position - player.pos;
            var options = _runOptionsProvider.Current;
            _env.Log($"[NecromancerChest] interaction-rejected reason={reason}; type={attempt.Waypoint.Kind}; " +
                $"attemptEntity={attempt.EntityId}; targetEntity={chest?.Object.EntityId}; targetKind={chest?.Kind}; " +
                $"targetGameObjectId={chest?.Object.GameObjectId}; distanceXZ={MathF.Sqrt(delta.X * delta.X + delta.Z * delta.Z):F2}; " +
                $"deltaY={delta.Y:F2}; maxDistance={GetInteractionDistance():F2}; " +
                $"calls={attempt.InteractionCallCount}; dispatches={attempt.NativeDispatchCount}; " +
                $"casting={_env.Casting()}; zoning={_env.Zoning()}; " +
                $"state={DescribeInteractionState()}; " +
                $"aggressive={_configuration.AggressiveChestInteraction}; gold={options.OpenGold}; silver={options.OpenSilver}; bronze={options.OpenBronze}");
            return false;
        }

		// Managed diagnostic-delegate failures must never suppress or lose a
		// real interaction — degrade the log field, keep the dispatch alive.
		private string DescribeInteractionState()
		{
			try { return _env.InteractionState(); }
			catch (Exception ex) { return $"unavailable:{ex.GetType().Name}"; }
		}
		private string DescribeLiveTargetable(FloorChestEvidence evidence)
		{
			try { return _env.LiveTargetable(evidence)?.ToString() ?? "unknown"; }
			catch (Exception ex) { return $"unavailable:{ex.GetType().Name}"; }
		}

		/// <summary>Ordinary wait (no rejection): logs the current lifecycle
		/// at most once per reason/3s. A default snapshot (reapproach)
		/// reports snapshot=none rather than pretending known evidence.</summary>
		private bool WaitForInteraction(ChestInteractionAttempt attempt, string reason, in ChestLifecycleSnapshot snapshot)
		{
			var now = _env.Now();
			if (attempt.LastWaitDiagnosticReason == reason &&
			    now < attempt.NextWaitDiagnosticAt)
				return false;
			attempt.LastWaitDiagnosticReason = reason;
			attempt.NextWaitDiagnosticAt = now.AddSeconds(3);
			var hasSnapshot = !snapshot.Equals(default);
			var ev = snapshot.Evidence;
			var elapsed = attempt.InteractionStartedAtUtc == DateTime.MinValue
				? "none"
				: (now - attempt.InteractionStartedAtUtc).TotalSeconds.ToString("0.0");
			var wp = attempt.Waypoint.Position;
			_env.Log($"[NecromancerChest] interaction-wait reason={reason}; " +
				$"waypoint={attempt.Waypoint.Kind}@({wp.X:F1},{wp.Y:F1},{wp.Z:F1}); " +
				$"entity={attempt.EntityId}; " +
				$"calls={attempt.InteractionCallCount}; dispatches={attempt.NativeDispatchCount}; " +
				(hasSnapshot
					? $"decision={snapshot.Decision.Status}; complete={snapshot.Decision.Complete}; " +
					  $"evidenceSeqAtStart={snapshot.EvidenceSequenceAtStart}; evidenceSeq={snapshot.EvidenceSequence}; " +
					  $"completionKind={ev.NativeCompletionKind}; targetable={ev.Object.IsTargetable}; " +
					  $"state={ev.State}; flags={ev.Flags}; "
					: "snapshot=none; ") +
				$"elapsedSinceInteraction={elapsed}s; " +
				$"nextRetryIn={(attempt.NextInteractAt - now).TotalSeconds:0.0}s");
			return false;
		}

		private static bool TryFindWaypointChestEvidence(
			FloorObjectEvidenceSnapshot evidence,
			ChestInteractionTarget waypoint,
			uint expectedEntityId,
			out FloorChestEvidence chest)
		{
			for (int i = 0; i < evidence.Chests.Count; i++)
			{
				chest = evidence.Chests[i];
				if (Vector3.Distance(chest.Object.Position, waypoint.Position) >= 0.5f)
					continue;

				if (chest.Kind != waypoint.Kind)
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

			if (GetInteractionRange().Contains(playerPosition, attempt.Waypoint.Position))
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

		internal float GetOpenTimeoutSeconds(ChestInteractionTarget waypoint)
		{
			if (waypoint.Kind == FloorChestKind.Banded)
				return BandedChestOpenTimeoutSeconds;

			return _configuration.AggressiveChestInteraction
				? AggressiveChestOpenTimeoutSeconds
				: NormalChestOpenTimeoutSeconds;
		}

		internal NavigationRange GetInteractionRange() => new(GetInteractionDistance());

		internal float GetInteractionDistance()
		{
			return Math.Clamp(_configuration.NecromancerChestInteractDistance, 0.5f, 2.0f);
		}

		/// <summary>Native-side chest position facts for the claim-only
		/// recovery path — range/LOS/targetability/player state plus the
		/// native waiting-for-reply decision; the OP never touches
		/// Service/BGCollision itself.</summary>
		internal Coord.ChestPositionObservation ObserveChestPosition(
			ChestInteractionAttempt attempt, FloorObjectEvidenceSnapshot? evidence)
		{
			var player = _env.PlayerState();
			FloorChestEvidence chest = default;
			var present = evidence?.Available == true &&
				TryFindWaypointChestEvidence(evidence, attempt.Waypoint,
					attempt.EntityId, out chest);
			var pos = present ? chest.Object.Position : attempt.Waypoint.Position;
			var range = GetInteractionRange();
			var inRange = present && player.present && range.Contains(player.pos, chest.Object.Position);
			var sightBlocked = present && player.present &&
				Runtime.Navigation.CombatPositionNavigation.Sight(
					player.pos, chest.Object.Position, out _)
					== Runtime.Navigation.CombatSight.Blocked;
			var decision = Observe(attempt, evidence).Decision;
			var waitingForReply = attempt.EntityId != 0 &&
				!decision.Complete && !decision.RetryInteraction;
			return new Coord.ChestPositionObservation(
				present, waitingForReply, inRange,
				sightBlocked,
				// Missing observation has UNKNOWN identity — never fabricate
			// a full GameObjectId from the cached waypoint attempt.
			present ? chest.Object.GameObjectId : 0UL, pos, range);
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

		private bool CanAttemptInteraction(bool aggressiveInteraction)
		{
			return (aggressiveInteraction || !_env.Casting()) && !_env.Zoning();
		}
    }
}

