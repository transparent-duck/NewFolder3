using System;
using System.Collections.Generic;
using Dalamud.Plugin.Services;
using MasterPlayer.MechanicRuntime;

namespace DeepDungeon.Fsd.Dalamud.Encounter;

/// <summary>
/// Dalamud host for the vendored mechanic runtime (AR-4.2/AR-4.4.a).
/// Feeds the world, runs modules, and publishes shared hints and drawings.
/// Movement remains owned by the coordinator's navigation surface.
/// </summary>
public sealed partial class EncounterMechanicHost : IEncounterSource, IDisposable
{
    public EncounterMechanicHost(global::Dalamud.Plugin.IDalamudPluginInterface pluginInterface)
    {
        ArgumentNullException.ThrowIfNull(pluginInterface);
        pluginInterface.Create<Service>(Array.Empty<object>());
    }

    private MechanicSession? _session;
    private MechanicWorldFeed? _feed;
    private BossMod.AIHintsBuilder? _hintsBuilder;
    private readonly BossMod.AIHints _aiHints = new();
    private DateTime _nextHintsRebuildUtc = DateTime.MinValue;
    private static readonly TimeSpan HintsRebuildInterval = TimeSpan.FromMilliseconds(100);
    private BossMod.BossModule? _hintModule;
    private bool _hasBossHints;
    private ushort _hintTerritory;
    private MechanicCastLocationCache? _castLocations;
    private MechanicMapEffectCapture? _mapEffects;
    private bool _initFailed, _disposed;
    private string? _lastLoggedError;
    private string? _lastHostError;
    private ulong _lastActiveModuleInstance;
    private string? _eventFailure;
    private ushort _healthTerritory = ushort.MaxValue;
    private (uint Dungeon, byte Floor) _healthFloor;

    public bool IsAvailable { get; private set; }
    public string? LastError { get; private set; }
    public string? ActiveModuleName { get; private set; }
    public int LastActorCount { get; private set; }
    public int LastDrawCommandCount { get; private set; }
    public IReadOnlyList<MechanicDrawCommand> LastDrawCommands { get; private set; } = [];

    private void EnsureSession()
    {
        if (_session != null || _initFailed)
            return;
        try
        {
            MechanicRuntimeEnvironment.Initialize(Service.DataManager.GameData, Service.Log);
            _events = new NativeEncounterEvents(this);
            BossMod.BossModuleManager.Config.MinMaturity = BossMod.BossModuleInfo.Maturity.WIP;
            BossMod.ZoneModuleManager.Config.MinMaturity = BossMod.BossModuleInfo.Maturity.WIP;
            _session = new MechanicSession(ResolveGameVersion());
            _feed = new MechanicWorldFeed(_session);
            _castLocations = new MechanicCastLocationCache();
            _hintsBuilder = new BossMod.AIHintsBuilder(_session.World, _session.BossModules, _session.ZoneModules, null);
            _mapEffects = new MechanicMapEffectCapture();
            Service.Log.Info("[Encounter] mechanic host initialized");
        }
        catch (Exception ex)
        {
            _initFailed = true;
            IsAvailable = false;
            LastError = $"Encounter initialization failed: {ex.Message}";
            Service.Log.Error(ex, "[Encounter] mechanic runtime init failed; host disabled");
            Detach();
            DisposeSessionResources();
        }
    }

    public void Update(IFramework _)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureSession();
        if (_initFailed || _session == null || _feed == null)
            return;
        try
        {
            var now = DateTime.UtcNow;
            var territory = (ushort)Service.ClientState.TerritoryType;
            if (_healthTerritory != territory)
            {
                _healthTerritory = territory;
                _healthFloor = default;
                _eventFailure = null;
            }
            var floor = ReadStableDeepDungeonFloor();
            if (floor.Floor != 0 && _healthFloor.Floor != 0 && floor != _healthFloor &&
                (_eventFailure != null || _castLocations?.Failure != null || _mapEffects?.Failure != null))
            {
                Service.Log.Information($"[Encounter] Rebuilding faulted input session on floor {_healthFloor.Floor} -> {floor.Floor}.");
                Reset();
                EnsureSession();
                if (_initFailed || _session == null || _feed == null) return;
                _healthTerritory = territory;
            }
            if (floor.Floor != 0) _healthFloor = floor;
            if (_events?.IsAvailable != true)
                throw new InvalidOperationException("Required ActionEffect/ActorControl capture is not enabled.");
            if (_eventFailure != null) throw new InvalidOperationException(_eventFailure);
            _castLocations?.BeginFrame((ushort)Service.ClientState.TerritoryType, now, Service.LocalPlayer != null);
            if (_castLocations?.Failure is { } castFailure) throw new InvalidOperationException(castFailure);
            var frame = MechanicWorldSampler.Capture(_castLocations, now, _session.Displacements);
            _session.BeginFrame(now);
            _feed.ApplyFrame(frame);
            _mapEffects?.Drain(_feed, frame.Territory, Service.LocalPlayer != null);
            if (_mapEffects?.Failure is { } mapFailure) throw new InvalidOperationException(mapFailure);
            if (_feed.Flush() != 0) _nextHintsRebuildUtc = DateTime.MinValue;
            _session.Update();
            UpdateZoneModuleCapture();
            UpdateForbiddenZones();
            UpdateExecutionRestrictions();
            IsAvailable = true;
            LastError = null;
            _lastHostError = null;
            LastActorCount = frame.Actors.Length;
            LogZoneAndModuleState(frame);
            UpdateDrawCapture();
        }
        catch (Exception ex)
        {
            ResetPlanningSnapshot();
            _aiHints.Clear();
            _execution?.Update(ExecutionRestrictions.None, DateTime.UtcNow);
            _localHazards = null;
            LastForbiddenZoneCount = LastBossForbiddenZoneCount = 0;
            _nextHintsRebuildUtc = DateTime.MinValue;
            LastError = ex.Message;
            IsAvailable = false;
            if (ex.Message != _lastHostError)
            {
                _lastHostError = ex.Message;
                Service.Log.Error(ex, "[Encounter] mechanic host frame update failed");
            }
        }
    }

    private static unsafe (uint Dungeon, byte Floor) ReadStableDeepDungeonFloor()
    {
        if (DeepDungeon.Fsd.Dalamud.GameState.DutyState.IsClientTransitioning || Service.LocalPlayer == null)
            return default;
        var framework = FFXIVClientStructs.FFXIV.Client.Game.Event.EventFramework.Instance();
        var dungeon = framework == null ? null : framework->GetInstanceContentDeepDungeon();
        return dungeon == null ? default : (dungeon->DeepDungeonId, dungeon->Floor);
    }

    public string? ActiveZoneModuleName { get; private set; }
    public int LastForbiddenZoneCount { get; private set; }
    public int LastBossForbiddenZoneCount { get; private set; }

    /// <summary>Internal replacement for the BMR ForbiddenZonesCount IPC:
    /// count of forbidden zones from the active zone module's AIHints.</summary>
    public int MobForbiddenZoneCount => LastForbiddenZoneCount;

    /// <summary>All forbidden zones the runtime currently wants the player out of
    /// (zone module + boss module).</summary>
    public int TotalForbiddenZoneCount => LastForbiddenZoneCount;

    private EncounterPlanningSnapshot? _localHazards;
    private DateTime _hintsCapturedAt;

    public EncounterPlanningSnapshot? GetLocalHazards()
    {
        if (_localHazards != null) return _localHazards;
        if (_aiHints.ForbiddenZones.Count == 0 || _session?.World.Party.Player() is not { } player)
            return null;
        return _localHazards = EncounterPlanningSnapshot.CaptureLocalHazards(
            _hintTerritory, _hintsCapturedAt, player.Position, _aiHints);
    }

    private ulong _lastActiveZoneModuleInstance;

    private ushort _lastLoggedTerritory = ushort.MaxValue;
    private int _lastLoggedLoadedCount = -1;
    private int _lastLoggedActorCount = -1;
    private DateTime _nextActorDiag;

    private void LogZoneAndModuleState(MechanicWorldFrame frame)
    {
        if (frame.Territory != _lastLoggedTerritory)
        {
            _lastLoggedTerritory = frame.Territory;
            _lastLoggedActorCount = -1;
            Service.Log.Info($"[Encounter] territory={frame.Territory} cfc={frame.ContentFinderConditionId}");
        }
        var loaded = _session?.BossModules.LoadedModules.Count ?? 0;
        if (loaded != _lastLoggedLoadedCount)
        {
            var prev = _lastLoggedLoadedCount;
            _lastLoggedLoadedCount = loaded;
            Service.Log.Info($"[Encounter] loaded boss modules: {prev} -> {loaded}" +
                (loaded > 0 ? $" ({_session!.BossModules.LoadedModules[^1].GetType().Name}, party={frame.Party.Length})" : ""));
        }
        var now = DateTime.UtcNow;
        if (frame.Actors.Length != _lastLoggedActorCount && now >= _nextActorDiag)
        {
            _nextActorDiag = now.AddSeconds(5);
            _lastLoggedActorCount = frame.Actors.Length;
            var enemies = 0;
            uint sampleOid = 0;
            foreach (var a in frame.Actors)
            {
                if (a.ObjectKind == 2 && a.SubKind == 5 && !a.IsDead)
                {
                    ++enemies;
                    if (sampleOid == 0)
                        sampleOid = a.OID;
                }
            }
            Service.Log.Debug($"[Encounter] actors={frame.Actors.Length} liveEnemies={enemies} sampleOid=0x{sampleOid:X}");
        }
    }

    private void UpdateZoneModuleCapture()
    {
        var zone = _session?.ZoneModules.ActiveModule;
        if (zone == null)
        {
            if (ActiveZoneModuleName != null)
            {
                Service.Log.Info($"[Encounter] zone module deactivated: {ActiveZoneModuleName}");
                ActiveZoneModuleName = null;
            }
            return;
        }
        var zoneType = zone.GetType().FullName ?? "unknown";
        if ((ulong)zone.GetHashCode() != _lastActiveZoneModuleInstance || ActiveZoneModuleName != zoneType)
        {
            _lastActiveZoneModuleInstance = (ulong)zone.GetHashCode();
            ActiveZoneModuleName = zoneType;
            Service.Log.Info($"[Encounter] zone module activated: {zoneType}");
        }
    }

    // One required builder for boss, automatic and zone hints. Failure is
    // reported to consumers; no reduced hint pipeline replaces it.
    private void UpdateForbiddenZones()
    {
        try
        {
            if (_session?.World.Party.Player() is not { } player)
            {
                _aiHints.Clear();
                _localHazards = null;
                LastForbiddenZoneCount = LastBossForbiddenZoneCount = 0;
                ResetPlanningSnapshot();
                _nextHintsRebuildUtc = DateTime.MinValue;
                _hintModule = null;
                return;
            }
            var now = _session.World.CurrentTime;
            var module = _session.BossModules.ActiveModule;
            var territory = _session.World.CurrentZone;
            var hasBossHints = module?.StateMachine.ActivePhase != null;
            if (now < _nextHintsRebuildUtc && ReferenceEquals(module, _hintModule)
                && territory == _hintTerritory && hasBossHints == _hasBossHints)
                return;
            _hintModule = module;
            _hasBossHints = hasBossHints;
            _hintTerritory = territory;
            _nextHintsRebuildUtc = now + HintsRebuildInterval;
            Potd180EncounterAdapter.BeforeHints(module);
            _session.UpdateHints(_hintsBuilder, _aiHints, 0, moveImminent: false);
            _localHazards = null;
            _hintsCapturedAt = now;
            var count = _aiHints.ForbiddenZones.Count;
            if (count != LastForbiddenZoneCount)
            {
                LastForbiddenZoneCount = count;
                Service.Log.Debug($"[Encounter] forbidden zones: {count}");
            }
            LastBossForbiddenZoneCount = module?.StateMachine.ActivePhase != null ? count : 0;
            UpdatePlanningSnapshot();
        }
        catch (Exception ex)
        {
            ResetPlanningSnapshot();
            _aiHints.Clear();
            _localHazards = null;
            LastForbiddenZoneCount = LastBossForbiddenZoneCount = 0;
            throw new InvalidOperationException($"Encounter hint build failed: {ex.Message}", ex);
        }
    }

    public EncounterReadiness GetReadiness(ulong actorId, uint oid, DateTime now)
    {
        if (LastError != null) return new(EncounterReadinessKind.Faulted, LastError);
        if (!IsAvailable || _session == null)
            return new(EncounterReadinessKind.Waiting, "Waiting for encounter host initialization.");
        var result = _session.GetReadiness(actorId, oid, _hintsCapturedAt, now);
        if (result.Kind == EncounterReadinessKind.Ready &&
            (_encounterSnapshot?.EncounterId != actorId || _encounterSnapshot.Hints == null))
            return new(EncounterReadinessKind.Faulted, "The active module has not published its planning snapshot.", _hintsCapturedAt);
        return result;
    }

    public EncounterFrame Read(ulong actorId = 0, uint oid = 0)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (actorId == 0 && _encounterSnapshot is { } snapshot)
        {
            actorId = snapshot.EncounterId;
            oid = snapshot.BossOID;
        }
        var readiness = GetReadiness(actorId, oid, DateTime.UtcNow);
        return new(_encounterSnapshot, GetLocalHazards(), readiness,
            MechanicExecutionState.Restrictions, _session?.World.CurrentTime ?? default);
    }

    /// <summary>Release a standalone inactive session. Borrowers never call this.</summary>
    public void Reset()
    {
        Detach();
        try { _execution?.Dispose(); }
        finally
        {
            _execution = null;
            DisposeSessionResources();
        }
        _initFailed = false;
        LastError = null;
        _eventFailure = null;
        _healthTerritory = ushort.MaxValue;
        _healthFloor = default;
    }

    private void UpdateDrawCapture()
    {
        var module = _session?.BossModules.ActiveModule;
        if (module == null)
        {
            if (ActiveModuleName != null)
            {
                Service.Log.Info($"[Encounter] mechanic module deactivated: {ActiveModuleName}");
                ActiveModuleName = null;
                LastDrawCommands = [];
                LastDrawCommandCount = 0;
                LastBossForbiddenZoneCount = 0;
            }
            return;
        }

        var moduleType = module.GetType().FullName ?? "unknown";
        if (module.GetHashCode() != (long)_lastActiveModuleInstance || ActiveModuleName != moduleType)
        {
            _lastActiveModuleInstance = (ulong)module.GetHashCode();
            ActiveModuleName = moduleType;
            Service.Log.Info($"[Encounter] mechanic module activated: {moduleType}");
        }

        try
        {
            var result = MechanicModuleDrawCapture.Capture(module, 0, new BossMod.Angle(0));
            LastDrawCommands = result.Commands;
            if (result.Commands.Count != LastDrawCommandCount)
                Service.Log.Debug($"[Encounter] boss draw commands: {result.Commands.Count}");
            LastDrawCommandCount = result.Commands.Count;
            if (result.Errors.Count > 0 && result.Errors[0].Exception.Message != _lastLoggedError)
            {
                _lastLoggedError = result.Errors[0].Exception.Message;
                Service.Log.Warning($"[Encounter] draw capture error stage={result.Errors[0].Stage}: {result.Errors[0].Exception.Message}");
            }
        }
        catch (Exception ex)
        {
            Service.Log.Warning($"[Encounter] draw capture failed: {ex.Message}");
        }

    }

    private static string ResolveGameVersion()
    {
        try
        {
            if (Service.DataManager.GameData.Repositories.TryGetValue("ffxiv", out var repo) && !string.IsNullOrEmpty(repo.Version))
                return repo.Version;
        }
        catch
        {
        }
        return "live";
    }

    public void Dispose()
    {
        if (_disposed) return;
        try { Reset(); }
        finally { _disposed = true; }
    }

    private void DisposeSessionResources()
    {
        try { _castLocations?.Dispose(); }
        catch (Exception ex) { Service.Log.Warning($"[Encounter] cast capture dispose error: {ex.Message}"); }
        _castLocations = null;
        try { _mapEffects?.Dispose(); }
        catch (Exception ex) { Service.Log.Warning($"[Encounter] map capture dispose error: {ex.Message}"); }
        _mapEffects = null;
        try { _hintsBuilder?.Dispose(); }
        catch (Exception ex) { Service.Log.Warning($"[Encounter] hints builder dispose error: {ex.Message}"); }
        _hintsBuilder = null;
        try { _session?.Dispose(); }
        catch (Exception ex) { Service.Log.Warning($"[Encounter] session dispose error: {ex.Message}"); }
        _session = null;
        _feed = null;
        ResetPlanningSnapshot();
        _localHazards = null;
        _aiHints.Clear();
        _hintModule = null;
        _hintsCapturedAt = default;
        _nextHintsRebuildUtc = DateTime.MinValue;
        LastDrawCommands = [];
        LastDrawCommandCount = LastForbiddenZoneCount = LastBossForbiddenZoneCount = 0;
        ActiveModuleName = ActiveZoneModuleName = null;
        IsAvailable = false;
    }
}
