using System;
using global::Dalamud.Plugin.Services;
using DeepDungeon.Fsd.Core;
using DeepDungeon.Fsd.Dalamud.GameState;
using DeepDungeon.Fsd.Dalamud.Runtime.Floor;
using DeepDungeon.Fsd.Dalamud.Runtime.Helpers;

namespace DeepDungeon.Fsd.Dalamud.Runtime
{
	/// <summary>
	/// Unified host that manages FSD scenarios.
	/// Layer 1: Container with flat state for FSD mode.
	/// Layer 2: MultiLoopState (FSD only) - survives loop completions.
	/// Layer 3: RunContext - per dungeon run lifecycle.
	/// DutyState and general assists (auto-spin, recovery potion) are owned by FsdEngine,
	/// not by this host. RunHost receives DutyState as a dependency.
	/// </summary>
	public sealed class RunHost : IDisposable
	{
		private readonly FsdSettings _configuration;

		private RunContext? _context;

		private MultiLoopDriver? _multiLoopDriver;

		private bool _isFsdMode = false;
		private IScenario? _attachedScenario = null;
	private Func<IScenario>? _scenarioFactory;
	private DetailedMapRunSnapshot? _detailedMapRunSnapshot;

	private readonly DutyState _dutyState;

	private readonly FloorPhaseController _floorController;
	private readonly ExitPolicyController _exit;
	private bool _inDutyControllersActive;

	private string _lastStatus = string.Empty;
	private bool _lastStatusIsError = false;
	private long _nextDutyAttemptGeneration;
	private readonly object _dutyAttemptEventLock = new();
	private long _activeDutyAttemptGeneration;
	private RunContext? _activeDutyAttemptContext;
	private IDutyState.DutyCompletedDelegate? _dutyCompletedHandler;
	private IDutyState.DutyWipedDelegate? _dutyWipedHandler;

		public RunHost(
			FsdSettings configuration,
			DutyState dutyState,
			NativeDeepDungeonLogMessageSource logMessageSource,
			IFloorEvidenceObserver? floorEvidenceObserver = null,
			IRunTelemetryObserver? runTelemetryObserver = null)
		{
			_configuration = configuration;
			_dutyState = dutyState;
			ArgumentNullException.ThrowIfNull(logMessageSource);
			_floorController = new FloorPhaseController(logMessageSource, floorEvidenceObserver, runTelemetryObserver);
		_exit = new ExitPolicyController();
	}

	public DutyState DutyState => _dutyState;

		public RunContext? Context => _context;
		public IRunOptionsProvider? RunOptionsProvider => _context?.RunOptions;
		public bool FsdActive => _isFsdMode;
		public bool AssistModeActive => _isFsdMode;
		public bool ScenarioAttached => _attachedScenario != null;

	// Expose shared controller for scenarios/panels that need fine-grained state.
	public FloorPhaseController FloorController => _floorController;

	public object ArmControlledReusableSaveSurveyCapture() =>
		_floorController.ArmControlledReusableSaveSurveyCapture();

		// ===== FSD API =====

		/// <summary>
		/// Start FSD mode with a scenario factory and loop settings.
		/// Creates RunContext and MultiLoopState.
		/// </summary>
		internal void StartFsd(
			Func<IScenario> scenarioFactory,
			int targetLoops,
			bool infinite,
			DetailedMapRunSnapshot detailedMapRunSnapshot)
		{
			if (_isFsdMode)
			{
				Service.Log.Warning("[RunHost] Cannot start FSD: another mode is already active");
				return;
			}

			try
			{
				StopFsd();
				
				_isFsdMode = true;
				_scenarioFactory = scenarioFactory;
				_detailedMapRunSnapshot = detailedMapRunSnapshot ??
					throw new ArgumentNullException(nameof(detailedMapRunSnapshot));
				_multiLoopDriver = new MultiLoopDriver(_configuration, targetLoops, infinite);
				_lastStatus = string.Empty;
				_lastStatusIsError = false;
				DeepDungeonUi.CloseDeepDungeonEntryWindows();
				DeepDungeonFloorsetTracker.Reset();

			// Initialize Layer 3: RunContext
			_context = new RunContext(
				_configuration,
				_dutyState,
				_detailedMapRunSnapshot);
			BindDutyAttemptEvents(_context);
			_floorController.Initialize(_context);
			_exit.Initialize(_context);
			_inDutyControllersActive = false;

			// Create first scenario instance
				_attachedScenario = _scenarioFactory();
				_attachedScenario.Initialize(_context);

				Service.Log.Info($"[RunHost] FSD started: {_attachedScenario.Name} (loops={(_multiLoopDriver!.InfiniteLoop ? "∞" : _multiLoopDriver.TargetLoops)})");
			}
			catch (Exception ex)
			{
				Service.Log.Error($"[RunHost] Failed to start FSD: {ex}");
				StopFsd();
			}
		}

		public void StopFsd()
		{
			lock (_dutyAttemptEventLock)
			{
				if (!_isFsdMode) return;
				_isFsdMode = false;
			}

			try
			{
				_floorController.CloseRunRecording("fsd-stopped", new
				{
					scenario = _attachedScenario?.Name ?? string.Empty,
					completedLoops = _multiLoopDriver?.CompletedLoops ?? 0,
					targetLoops = _multiLoopDriver?.TargetLoops ?? 0,
					infinite = _multiLoopDriver?.InfiniteLoop ?? false
				});
			}
			catch (Exception ex)
			{
				try { Service.Log.Error($"[RunHost] Failed to close run recording while stopping FSD: {ex}"); } catch { }
			}
			try { _floorController.Dispose(); } catch { }
			try { _exit.Dispose(); } catch { }
			_inDutyControllersActive = false;
			try { _attachedScenario?.Dispose(); } catch { }
			_attachedScenario = null;
			_scenarioFactory = null;
			_detailedMapRunSnapshot = null;

			ReleaseDutyAttemptEvents();
			try { _context?.Dispose(); } catch { }
			_context = null;

			_multiLoopDriver = null;
			DeepDungeonUi.CloseDeepDungeonEntryWindows();

			Service.Log.Info("[RunHost] FSD stopped");
		}

		// ===== Update Loop =====

		public void Update(IFramework framework)
		{
			if (!_isFsdMode)
				return;

			if (_context != null)
			{
				if (_context.Duty.StateReadFailed)
				{
					SetDutyStateUnavailableStatus(_context.Duty.LastStateReadError);
					return;
				}
			}

			// FSD mode: scenario drives entry/exit flows
			if (_isFsdMode)
			{
				UpdateFsdMode(framework);
				return;
			}
		}

		private void UpdateFsdMode(IFramework framework)
		{
			if (_attachedScenario == null || _context == null || _multiLoopDriver == null)
			{
				StopFsd();
				return;
			}

			try
			{
				_multiLoopDriver.ObserveDutyState(_dutyState);
				MarkPlayerDeathFatalIfObserved();
				StopInDutyMovementAfterDutyExit();
				_attachedScenario.Update(framework);

				_lastStatus = _context.StatusLine ?? string.Empty;
				_lastStatusIsError = _context.StatusIsError;

				if (_context.Duty.IsInDuty && !_context.StatusIsError)
					UpdateControllers(framework);

				if (_attachedScenario.IsComplete)
				{
					if (_context.StatusIsError)
					{
						_floorController.CloseRunRecording("fsd-scenario-error", new
						{
							scenario = _attachedScenario.Name,
							status = _context.StatusLine ?? string.Empty
						});
						StopFsd();
						return;
					}

					if ((_attachedScenario.RequiresDutyCompletionEvent && !_context.DutyCompletionObserved) ||
					    _context.DutyFailureObserved)
					{
						string status = "Deep Dungeon run ended without a valid completion event; loop will not be counted.";
						_context.StatusLine = status;
						_context.StatusIsError = true;
						_lastStatus = status;
						_lastStatusIsError = true;
						_floorController.CloseRunRecording("fsd-scenario-error", new
						{
							scenario = _attachedScenario.Name,
							status
						});
						StopFsd();
						return;
					}

					if (_multiLoopDriver.ShouldStopAfterCurrentRun(_dutyState))
					{
						string stopReason = _multiLoopDriver.LastStopReason;
						if (!string.IsNullOrWhiteSpace(stopReason))
						{
							_lastStatus = stopReason;
							_lastStatusIsError = _multiLoopDriver.LastStopWasError;
							_context.StatusLine = stopReason;
							_context.StatusIsError = _multiLoopDriver.LastStopWasError;
						}

						_floorController.CloseRunRecording("fsd-final-loop-complete", new
						{
							scenario = _attachedScenario.Name,
							completedLoops = _multiLoopDriver.CompletedLoops + 1,
							targetLoops = _multiLoopDriver.TargetLoops,
							infinite = _multiLoopDriver.InfiniteLoop,
							stopReason,
							stopWasError = _multiLoopDriver.LastStopWasError
						});
						StopFsd();
					}
					else if (_attachedScenario.ShouldLoop)
					{
						_multiLoopDriver.IncrementLoop();
						Service.Log.Info($"[RunHost] Loop complete. Progress: ({_multiLoopDriver.CompletedLoops}/{(_multiLoopDriver.InfiniteLoop ? "∞" : _multiLoopDriver.TargetLoops)})");

						_floorController.CloseRunRecording("fsd-loop-complete", new
						{
							scenario = _attachedScenario.Name,
							completedLoops = _multiLoopDriver.CompletedLoops,
							targetLoops = _multiLoopDriver.TargetLoops,
							infinite = _multiLoopDriver.InfiniteLoop
						});

						try { _attachedScenario.Dispose(); } catch { }
						try { _floorController.Dispose(); } catch { }
						try { _exit.Dispose(); } catch { }
						_inDutyControllersActive = false;
						ReleaseDutyAttemptEvents();
						try { _context?.Dispose(); } catch { }

						DeepDungeonFloorsetTracker.Reset();
						_context = new RunContext(
							_configuration,
							_dutyState,
							_detailedMapRunSnapshot ??
								throw new InvalidOperationException(
									"Detailed-map run snapshot was released before loop recreation."));
						BindDutyAttemptEvents(_context);
						_floorController.Initialize(_context);
						_exit.Initialize(_context);
						_inDutyControllersActive = false;

						_attachedScenario = _scenarioFactory!.Invoke();
						_attachedScenario.Initialize(_context);
					}
					else
					{
						StopFsd();
					}
				}
			}
			catch (Exception ex)
			{
				Service.Log.Error($"[RunHost] FSD update error: {ex}");
				StopFsd();
			}
		}

	/// <summary>
	/// Update in-duty controllers during an FSD scenario.
	/// </summary>
	private void UpdateControllers(IFramework framework)
	{
		if (_context == null || !_context.Duty.IsInDuty)
			return;

		bool assistActive = AssistModeActive;
		_inDutyControllersActive = true;

		if (assistActive)
		{
			try
			{
				_exit.Evaluate();
			}
			catch (Exception ex)
			{
				try { Service.Log.Error($"[RunHost] Exit policy error: {ex}"); } catch { }
			}
		}

		if (_exit.IsLeaveActive)
		{
			try { _floorController.CancelActiveMovement(); } catch { }
			try { _context.Navigator.CancelAll(); } catch { }
			try
			{
				_exit.Update(framework);
			}
			catch (Exception ex)
			{
				try { Service.Log.Error($"[RunHost] Leave flow error: {ex}"); } catch { }
			}
			return;
		}

		// Unified floor controller (search + passage combined)
		if (assistActive)
		{
			try
			{
				_floorController.Update(framework);
			}
			catch (Exception ex)
			{
				try { Service.Log.Error($"[RunHost] Floor controller error: {ex}"); } catch { }
				return;
			}
		}

			// Auto-open chests when assist mode is active
			if (assistActive)
			{
				try
				{
					_floorController.TickInteractionChannel(framework);
				}
				catch (Exception ex)
				{
					try { Service.Log.Error($"[RunHost] Chest interaction channel error: {ex}"); } catch { }
				}
			}

			if (assistActive)
				_floorController.TickCombatChannel(out _);

		}

	private void SetDutyStateUnavailableStatus(string error)
	{
		string reason = string.IsNullOrWhiteSpace(error) ? "unknown error" : error;
		string status = $"Duty state unavailable: {reason}";
		_lastStatus = status;
		_lastStatusIsError = true;
		if (_context != null)
		{
			_context.StatusLine = status;
			_context.StatusIsError = true;
		}
	}

	private void MarkPlayerDeathFatalIfObserved()
	{
		if (_context == null || !_context.Duty.IsInDuty || _context.StatusIsError)
			return;

		var player = Service.LocalPlayer;
		if (player == null || !player.IsDead)
			return;

		_context.MarkDutyFailed();
		string status = "Deep Dungeon run failed: player died; manual leave required.";
		_context.StatusLine = status;
		_context.StatusIsError = true;
		_lastStatus = status;
		_lastStatusIsError = true;
		_floorController.CloseRunRecording("player-death-fatal", new
		{
			floor = _context.Duty.Floor,
			dungeonId = _context.Duty.DungeonId,
			phase = _floorController.CurrentPhase.ToString(),
			status = _floorController.Status
		});
		try { _floorController.Dispose(); } catch { }
		try { _exit.Dispose(); } catch { }
		try { _context.Navigator.CancelAll(); } catch { }
		_inDutyControllersActive = false;
		Service.Log.Warning("[RunHost] Deep Dungeon run marked fatal because the local player died.");
	}

	private void StopInDutyMovementAfterDutyExit()
	{
		var context = _context;
		if (context == null || context.Duty.IsInDuty || !_inDutyControllersActive)
			return;

		try { _floorController.CancelActiveMovement(); } catch { }
		try { context.Navigator.CancelAll(); } catch { }
		_inDutyControllersActive = false;
	}

		public string CurrentScenarioName => _attachedScenario?.Name ?? string.Empty;
		public int CompletedLoops => _multiLoopDriver?.CompletedLoops ?? 0;
		public int TargetLoops => _multiLoopDriver?.TargetLoops ?? 0;
		public bool Infinite => _multiLoopDriver?.InfiniteLoop ?? false;
		public string CurrentStatus => _context?.StatusLine ?? string.Empty;
		public bool CurrentStatusIsError => _context?.StatusIsError ?? false;
		public string LastStatus => _lastStatus;
		public bool LastStatusIsError => _lastStatusIsError;

		public (bool running, string scenario, int completed, int target, bool infinite, bool inDuty, uint dungeonId, byte floor, bool passageOpen) GetStatusSnapshot()
		{
			bool running = _isFsdMode;
			if (!running || _context == null)
				return (false, string.Empty, 0, 0, false, false, 0, 0, false);
			
			var d = _context.Duty;
			return (true, CurrentScenarioName, CompletedLoops, TargetLoops, Infinite, d.IsInDuty, d.DungeonId, d.Floor, d.PassageOpen);
		}

		// ===== Dispose =====

	public void Dispose()
	{
		StopFsd();
		ReleaseDutyAttemptEvents();

		try { _floorController.Dispose(); } catch { }
		try { _exit.Dispose(); } catch { }
	}

	private void BindDutyAttemptEvents(RunContext context)
	{
		ReleaseDutyAttemptEvents();
		lock (_dutyAttemptEventLock)
		{
			long generation = ++_nextDutyAttemptGeneration;
			IDutyState.DutyCompletedDelegate completed = args => OnDutyCompleted(context, generation, args);
			IDutyState.DutyWipedDelegate wiped = args => OnDutyWiped(context, generation, args);
			_activeDutyAttemptGeneration = generation;
			_activeDutyAttemptContext = context;
			_dutyCompletedHandler = completed;
			_dutyWipedHandler = wiped;

			try
			{
				Service.DutyState.DutyCompleted += completed;
				Service.DutyState.DutyWiped += wiped;
			}
			catch
			{
				try { Service.DutyState.DutyCompleted -= completed; } catch { }
				try { Service.DutyState.DutyWiped -= wiped; } catch { }
				_activeDutyAttemptGeneration = 0;
				_activeDutyAttemptContext = null;
				_dutyCompletedHandler = null;
				_dutyWipedHandler = null;
				throw;
			}
		}
	}

	private void ReleaseDutyAttemptEvents()
	{
		lock (_dutyAttemptEventLock)
		{
			var completed = _dutyCompletedHandler;
			var wiped = _dutyWipedHandler;
			if (completed == null && wiped == null)
				return;

			_activeDutyAttemptGeneration = 0;
			_activeDutyAttemptContext = null;
			_dutyCompletedHandler = null;
			_dutyWipedHandler = null;
			try
			{
				if (completed != null)
					Service.DutyState.DutyCompleted -= completed;
			}
			catch (Exception ex)
			{
				try { Service.Log.Error($"[RunHost] Failed to unsubscribe DutyCompleted for the duty attempt: {ex}"); } catch { }
			}

			try
			{
				if (wiped != null)
					Service.DutyState.DutyWiped -= wiped;
			}
			catch (Exception ex)
			{
				try { Service.Log.Error($"[RunHost] Failed to unsubscribe DutyWiped for the duty attempt: {ex}"); } catch { }
			}
		}
	}

	private void OnDutyCompleted(RunContext attemptContext, long attemptGeneration, global::Dalamud.Game.DutyState.IDutyStateEventArgs args)
	{
		lock (_dutyAttemptEventLock)
		{
			try
			{
				if (!CanAcceptDutyAttemptEvent(attemptContext, attemptGeneration, args, out var rejectionReason))
				{
					RecordRejectedDutyAttemptEvent("DutyCompleted", attemptGeneration, args, rejectionReason);
					return;
				}

				var player = Service.LocalPlayer;
				if (player != null && player.IsDead)
				{
					MarkDutyWipeFatal(attemptContext, "Deep Dungeon run failed: duty completion event arrived while the player was dead.");
					return;
				}

				attemptContext.MarkDutyCompleted();
			}
			catch (Exception ex)
			{
				try { Service.Log.Error($"[RunHost] DutyCompleted handler failed: {ex}"); } catch { }
			}
		}
	}

	private void OnDutyWiped(RunContext attemptContext, long attemptGeneration, global::Dalamud.Game.DutyState.IDutyStateEventArgs args)
	{
		lock (_dutyAttemptEventLock)
		{
			try
			{
				if (!CanAcceptDutyAttemptEvent(attemptContext, attemptGeneration, args, out var rejectionReason))
				{
					RecordRejectedDutyAttemptEvent("DutyWiped", attemptGeneration, args, rejectionReason);
					return;
				}

				MarkDutyWipeFatal(attemptContext, "Deep Dungeon run failed: duty wipe observed; manual leave required.");
			}
			catch (Exception ex)
			{
				try { Service.Log.Error($"[RunHost] DutyWiped handler failed: {ex}"); } catch { }
			}
		}
	}

	private bool CanAcceptDutyAttemptEvent(
		RunContext attemptContext,
		long attemptGeneration,
		global::Dalamud.Game.DutyState.IDutyStateEventArgs args,
		out string rejectionReason)
	{
		if (!_isFsdMode)
		{
			rejectionReason = "host-inactive";
			return false;
		}

		if (attemptGeneration == 0 || attemptGeneration != _activeDutyAttemptGeneration)
		{
			rejectionReason = "stale-generation";
			return false;
		}

		if (!ReferenceEquals(attemptContext, _activeDutyAttemptContext) || !ReferenceEquals(attemptContext, _context))
		{
			rejectionReason = "stale-context";
			return false;
		}

		if (!attemptContext.Duty.IsInDuty || attemptContext.Duty.DungeonId == 0)
		{
			rejectionReason = "attempt-not-in-duty";
			return false;
		}

		uint eventTerritoryId = args.TerritoryType.RowId;
		if (eventTerritoryId == 0 || eventTerritoryId != Service.ClientState.TerritoryType)
		{
			rejectionReason = "event-territory-not-current";
			return false;
		}

		if (!DungeonCatalog.TryGetByTerritoryId(eventTerritoryId, out var eventDungeon) ||
		    eventDungeon.DungeonId != attemptContext.Duty.DungeonId)
		{
			rejectionReason = "event-duty-mismatch";
			return false;
		}

		rejectionReason = string.Empty;
		return true;
	}

	private static void RecordRejectedDutyAttemptEvent(
		string eventName,
		long attemptGeneration,
		global::Dalamud.Game.DutyState.IDutyStateEventArgs args,
		string rejectionReason)
	{
		try
		{
			Service.Log.Warning(
				$"[RunHost] Rejected {eventName} for duty attempt {attemptGeneration}: {rejectionReason}; " +
				$"territory={args.TerritoryType.RowId}, contentFinder={args.ContentFinderCondition.RowId}, eventHandler={args.EventHandlerId}");
		}
		catch { }
	}

	private void MarkDutyWipeFatal(RunContext context, string status)
	{
		try
		{
			if (context.StatusIsError)
				return;

			context.MarkDutyFailed();
			context.StatusLine = status;
			context.StatusIsError = true;
			_lastStatus = status;
			_lastStatusIsError = true;
			try
			{
				_floorController.CloseRunRecording("duty-wipe-fatal", new
				{
					floor = context.Duty.Floor,
					dungeonId = context.Duty.DungeonId,
					phase = _floorController.CurrentPhase.ToString(),
					status = _floorController.Status
				});
			}
			catch (Exception ex)
			{
				try { Service.Log.Error($"[RunHost] Failed to close run recording after DutyWiped: {ex}"); } catch { }
			}
			try { _floorController.Dispose(); } catch { }
			try { _exit.Dispose(); } catch { }
			try { context.Navigator.CancelAll(); } catch { }
			_inDutyControllersActive = false;
			Service.Log.Warning("[RunHost] Deep Dungeon run marked fatal because DutyWiped was observed.");
		}
		catch (Exception ex)
		{
			try { Service.Log.Error($"[RunHost] Failed to apply fatal DutyWiped state: {ex}"); } catch { }
		}
	}
	}
}
