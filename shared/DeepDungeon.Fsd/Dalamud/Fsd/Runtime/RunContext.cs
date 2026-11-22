using System;
using System.Collections.Generic;
using System.Threading;
using DeepDungeon.Fsd.Core;
using DeepDungeon.Fsd.Dalamud.GameState;
using DeepDungeon.Fsd.Dalamud.Runtime.Combat;
using DeepDungeon.Fsd.Dalamud.Runtime.Helpers;
using DeepDungeon.Fsd.Dalamud.Runtime.Navigation;
using DeepDungeon.Fsd.Dalamud.Runtime.Scenarios;
using global::Dalamud.Game.ClientState.Objects.Types;

namespace DeepDungeon.Fsd.Dalamud.Runtime
{
	/// <summary>
	/// Per-loop instance context; contains runtime services and cancellation.
	/// Created once per dungeon run, survives until duty exit.
	/// </summary>
	public sealed class RunContext : IDisposable
	{
		public readonly FsdSettings Configuration;
        public readonly MasterPlayer.MechanicRuntime.IEncounterSource Encounters;
		public readonly CancellationTokenSource CancellationSource;

		// Services
		public readonly DutyState Duty;
		internal readonly Func<IBattleChara, bool> CanSelectNewCombatTarget;
		public readonly DeepDungeonUi Ui;
		public readonly SaveSlotManager SaveSlots;
		public readonly INavigator Navigator;
		public readonly IRunOptionsProvider RunOptions;
		internal readonly DetailedMapRunSnapshot DetailedMap;
		/// <summary>Scoped run/floor knowledge — the single owner of hoard
		/// facts, work completion truth and the observed collection rate.</summary>
		internal RunKnowledge Knowledge { get; } = new();
		internal readonly CombatAssistPolicy CombatAssist;
        internal ulong? ClearingTargetId { get; set; }
		internal readonly FsdChestInteraction ChestInteraction;
        internal DeepDungeon.Fsd.Core.FarmingPlan? FarmingPlan;
        internal Action<string, object>? RecordDiagnostic;
        internal long CombatPreparationSequence { get; private set; }
        private ulong _preparedCombatTarget;
        private bool _rotationWasInCombat;

        internal void PrepareCombat(ulong targetId)
        {
            if (DutyCompletionObserved || targetId == 0 || targetId == _preparedCombatTarget) return;
            _preparedCombatTarget = targetId;
            CombatPreparationSequence++;
        }

        internal void ObserveCombatState(bool inCombat)
        {
            if (_rotationWasInCombat && !inCombat) _preparedCombatTarget = 0;
            _rotationWasInCombat = inCombat;
        }
        internal bool HarvestComplete;
        internal bool TerminalRewardsRequired;
        internal bool TerminalRoomObserved;
        internal bool TerminalReviewRequested;
        internal bool TerminalStayRequested;
        internal bool AttemptAborted;
        internal string AttemptAbortReason = string.Empty;
        internal int HoardDiscoveries;
        internal string PreparedSaveDescription = string.Empty;

		// Status
		public string StatusLine = string.Empty;
		public bool StatusIsError = false;
		internal bool EntryReadyForDuty = true;
		public bool DutyCompletionObserved { get; private set; }
		internal long DutyCompletionSequence { get; private set; }
		public bool DutyFailureObserved { get; private set; }

		private readonly object _suppressedCombatTargetsLock = new();
		private readonly Dictionary<ulong, DateTime> _suppressedCombatTargets = new();

	internal RunContext(
        MasterPlayer.MechanicRuntime.IEncounterSource encounterSource,
		FsdSettings configuration,
		DutyState dutyState,
		DetailedMapRunSnapshot detailedMap)
	{
		Encounters = encounterSource ?? throw new ArgumentNullException(nameof(encounterSource));
		Configuration = configuration;
		DetailedMap = detailedMap ??
			throw new ArgumentNullException(nameof(detailedMap));
		CancellationSource = new CancellationTokenSource();
			Duty = dutyState;
			CanSelectNewCombatTarget = IsNewCombatTargetAllowed;
			Ui = new DeepDungeonUi();
			SaveSlots = new SaveSlotManager();
			Navigator = new NavigatorVNavAdapter();
			CombatAssist = new CombatAssistPolicy();
			var defaults = new RunOptions
			{
				OpenGold = configuration.NecromancerAutoOpenGoldChest,
				OpenSilver = configuration.NecromancerAutoOpenSilverChest,
				OpenBronze = configuration.NecromancerAutoOpenBronzeChest,
				BandedEnabled = configuration.NecromancerAutoBandedFarmEnabled,
				LeaveMode = LeaveModeUiMapping.FromUiIndex(configuration.NecromancerAutoLeaveMode),
				LeaveAfterMinutes = Math.Max(0, configuration.NecromancerAutoLeaveAfterMinutes)
			};
			RunOptions = new RunOptionsProvider(defaults);
			ChestInteraction = new FsdChestInteraction(configuration, RunOptions);
		}

	public CancellationToken Token => CancellationSource.Token;

    internal bool IsPullExcluded(IBattleChara target) =>
        !DeepDungeon.Fsd.Core.MobPullPolicy.CanInitiate(FarmingPlan?.Mode, Duty.DungeonId, Duty.Floor, target.NameId);

    // Existing hate is an obligation, even for a mob we would not voluntarily pull.
    internal bool IsNewCombatTargetAllowed(IBattleChara target) =>
        !IsPullExcluded(target) || Floor.EnemyChaseHelper.IsAggroedToPlayer(target.GameObjectId);

	public void Cancel()
	{
		try { CancellationSource.Cancel(); } catch { }
	}

	/// <summary>
	/// Reset per-attempt state (error flags and observations) for a fresh start.
	/// Called by scenarios on Initialize() to clear stale state from previous attempts.
	/// 
	/// Does NOT reset:
	/// - DutyStateWatcher (tracks multi-loop state like floor, hoard count)
	/// - Navigator (persistent VNavmesh connection)
	/// - Configuration / RunOptions (user settings)
	/// </summary>
	public void ResetAttemptState()
	{
		// Clear error state
		StatusIsError = false;
		StatusLine = string.Empty;
		DutyCompletionObserved = false;
		DutyCompletionSequence = 0;
		DutyFailureObserved = false;
		
		ClearSuppressedCombatTargets();
		
		// Note: CancellationSource is NOT reset here because it's created once per RunContext lifetime
		// If we need cancellation, we dispose and create a new RunContext
	}

	public void Dispose()
	{
		Knowledge.Reset();
		Cancel();
		try { (Navigator as IDisposable)?.Dispose(); }
		catch (Exception ex)
		{
			try { Service.Log.Error($"[RunContext] Failed to dispose navigator: {ex}"); } catch { }
		}
		try { CancellationSource.Dispose(); }
		catch (Exception ex)
		{
			try { Service.Log.Error($"[RunContext] Failed to dispose cancellation source: {ex}"); } catch { }
		}
	}

	public void MarkDutyCompleted()
	{
		if (DutyFailureObserved)
			return;

		DutyCompletionObserved = true;
		DutyCompletionSequence++;
	}

    internal bool TryHoldFailedAttempt(string reason)
    {
        if (FarmingPlan?.HoldOnFailure != true) return false;
        StatusLine = $"{reason}；FSD 已停止，保留副本與存檔，等待現場觀察。";
        StatusIsError = true;
        Navigator.CancelAll();
        Service.Log.Warning($"[Farming] Diagnostic attempt held: floor={Duty.Floor}, reason={reason}");
        return true;
    }

	public void MarkDutyFailed()
	{
		DutyFailureObserved = true;
		DutyCompletionObserved = false;
	}

	public void SuppressCombatTarget(ulong targetId, TimeSpan? ttl = null)
	{
		if (targetId == 0)
			return;

		lock (_suppressedCombatTargetsLock)
		{
			PruneSuppressedCombatTargetsLocked();
			_suppressedCombatTargets[targetId] = DateTime.UtcNow + (ttl ?? TimeSpan.FromSeconds(20));
		}
	}

	public bool IsCombatTargetSuppressed(ulong targetId)
	{
		if (targetId == 0)
			return false;

		lock (_suppressedCombatTargetsLock)
		{
			PruneSuppressedCombatTargetsLocked();
			return _suppressedCombatTargets.ContainsKey(targetId);
		}
	}

	public void ClearSuppressedCombatTargets()
	{
		lock (_suppressedCombatTargetsLock)
		{
			_suppressedCombatTargets.Clear();
		}
	}

	private void PruneSuppressedCombatTargetsLocked()
	{
		if (_suppressedCombatTargets.Count == 0)
			return;

		var now = DateTime.UtcNow;
		Span<ulong> expired = stackalloc ulong[Math.Min(_suppressedCombatTargets.Count, 32)];
		int expiredCount = 0;
		foreach (var pair in _suppressedCombatTargets)
		{
			if (pair.Value > now)
				continue;

			if (expiredCount >= expired.Length)
				break;

			expired[expiredCount++] = pair.Key;
		}

		for (int i = 0; i < expiredCount; i++)
			_suppressedCombatTargets.Remove(expired[i]);
	}
	}
}

