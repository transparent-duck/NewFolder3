using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using DeepDungeon.Fsd.Dalamud.GameState;
using DeepDungeon.Fsd.Dalamud.Runtime.Combat;
using DeepDungeon.Fsd.Dalamud.Runtime.Helpers;
using DeepDungeon.Fsd.Dalamud.Runtime.Navigation;
using DeepDungeon.Fsd.Dalamud.Runtime.Scenarios;

namespace DeepDungeon.Fsd.Dalamud.Runtime
{
	/// <summary>
	/// Per-loop instance context; contains runtime services and cancellation.
	/// Created once per dungeon run, survives until duty exit.
	/// </summary>
	public sealed class RunContext : IDisposable
	{
		public readonly FsdSettings Configuration;
		public readonly CancellationTokenSource CancellationSource;

		// Services
		public readonly DutyState Duty;
		public readonly DeepDungeonUi Ui;
		public readonly SaveSlotManager SaveSlots;
		public readonly INavigator Navigator;
		public readonly IRunOptionsProvider RunOptions;
		internal readonly DetailedMapRunSnapshot DetailedMap;
		internal readonly CombatAssistPolicy CombatAssist;
		internal readonly FsdChestInteraction ChestInteraction;
		internal ControlledPtSurveySession? ControlledPtSurvey;

		// Status
		public string StatusLine = string.Empty;
		public bool StatusIsError = false;
		public bool DutyCompletionObserved { get; private set; }
		public bool DutyFailureObserved { get; private set; }

		private readonly object _preferredAggroLock = new();
		private ulong _preferredAggroTargetId;
		private Vector3 _preferredAggroTargetPosition;
		private DateTime _preferredAggroExpiry = DateTime.MinValue;
		private readonly Dictionary<ulong, DateTime> _suppressedCombatTargets = new();

	internal RunContext(
		FsdSettings configuration,
		DutyState dutyState,
		DetailedMapRunSnapshot detailedMap)
	{
		Configuration = configuration;
		DetailedMap = detailedMap ??
			throw new ArgumentNullException(nameof(detailedMap));
		CancellationSource = new CancellationTokenSource();
			Duty = dutyState;
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
		DutyFailureObserved = false;
		
		ClearSuppressedCombatTargets();
		
		// Note: CancellationSource is NOT reset here because it's created once per RunContext lifetime
		// If we need cancellation, we dispose and create a new RunContext
	}

	public void Dispose()
	{
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
	}

	public void MarkDutyFailed()
	{
		DutyFailureObserved = true;
		DutyCompletionObserved = false;
	}

	public void SetPreferredAggroTarget(ulong targetId, Vector3 position, TimeSpan? ttl = null)
	{
		if (targetId == 0)
			return;

		var expiry = DateTime.Now + (ttl ?? TimeSpan.FromSeconds(6));
		lock (_preferredAggroLock)
		{
			_preferredAggroTargetId = targetId;
			_preferredAggroTargetPosition = position;
			_preferredAggroExpiry = expiry;
		}
	}

	public void ClearPreferredAggroTarget()
	{
		lock (_preferredAggroLock)
		{
			_preferredAggroTargetId = 0;
			_preferredAggroTargetPosition = default;
			_preferredAggroExpiry = DateTime.MinValue;
		}
	}

	public bool TryGetPreferredAggroTarget(out ulong targetId)
	{
		lock (_preferredAggroLock)
		{
			if (_preferredAggroTargetId != 0 && DateTime.Now <= _preferredAggroExpiry)
			{
				targetId = _preferredAggroTargetId;
				return true;
			}
		}

		targetId = 0;
		return false;
	}

	public bool TryGetPreferredAggroTarget(out ulong targetId, out Vector3 position)
	{
		lock (_preferredAggroLock)
		{
			if (_preferredAggroTargetId != 0 && DateTime.Now <= _preferredAggroExpiry)
			{
				targetId = _preferredAggroTargetId;
				position = _preferredAggroTargetPosition;
				return true;
			}
		}

		targetId = 0;
		position = default;
		return false;
	}

	public void SuppressCombatTarget(ulong targetId, TimeSpan? ttl = null)
	{
		if (targetId == 0)
			return;

		lock (_preferredAggroLock)
		{
			PruneSuppressedCombatTargetsLocked();
			_suppressedCombatTargets[targetId] = DateTime.UtcNow + (ttl ?? TimeSpan.FromSeconds(20));
		}
	}

	public bool IsCombatTargetSuppressed(ulong targetId)
	{
		if (targetId == 0)
			return false;

		lock (_preferredAggroLock)
		{
			PruneSuppressedCombatTargetsLocked();
			return _suppressedCombatTargets.ContainsKey(targetId);
		}
	}

	public void ClearSuppressedCombatTargets()
	{
		lock (_preferredAggroLock)
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

