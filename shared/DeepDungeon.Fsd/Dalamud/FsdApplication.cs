using DeepDungeon.Fsd.Dalamud.Encounter;
using DeepDungeon.Fsd.Dalamud.Movement;
using MasterPlayer.MechanicRuntime;
using global::Dalamud.Plugin;
using global::Dalamud.Plugin.Services;
using DeepDungeon.Fsd.Core;
using DeepDungeon.Fsd.Dalamud.Runtime;
using DeepDungeon.Fsd.Dalamud.Runtime.Navigation;

namespace DeepDungeon.Fsd.Dalamud;

public sealed partial class FsdApplication : IFsdApplication
{
    private readonly IEncounterSource _encounters;
    private EncounterMechanicHost? _ownedEncounter;
    private readonly SpinningMob _spin = new();
    public SpinningMob BossSpin => _spin;
    private readonly string _hostIdentity;
    private readonly string _hostVersion;
    private readonly DetailedMapHostOptions _detailedMapHostOptions;
    private readonly FsdSettings _settings;
    private readonly FsdExecutionLease _lease;
    private readonly FsdEngine _module;
    private readonly Func<float> _playerYOffsetProvider;
    private readonly FsdFacadeLifecycle _lifecycle = new();

    public FsdApplication(
        IDalamudPluginInterface pluginInterface,
        IFsdSettingsStore settingsStore,
        string hostIdentity,
        string hostVersion,
        DetailedMapHostOptions detailedMapHostOptions,
        IEncounterSource encounterSource,
        Func<float> playerYOffsetProvider,
        IFloorEvidenceObserver? floorEvidenceObserver = null,
        IRunTelemetryObserver? runTelemetryObserver = null,
        FsdStartAuthorizationCallback? tryAuthorizeFsdStart = null,
        Func<string?>? fsdStartDenialNoticeProvider = null,
        Action<DeepDungeonStateSnapshot>? stateObserver = null)
    {
        ArgumentNullException.ThrowIfNull(encounterSource);
        _encounters = encounterSource;
        ArgumentNullException.ThrowIfNull(pluginInterface);
        ArgumentNullException.ThrowIfNull(settingsStore);
        ArgumentNullException.ThrowIfNull(detailedMapHostOptions);
        ArgumentNullException.ThrowIfNull(playerYOffsetProvider);
        if (string.IsNullOrWhiteSpace(hostIdentity))
            throw new ArgumentException("FSD host identity is required.", nameof(hostIdentity));
        if (string.IsNullOrWhiteSpace(hostVersion))
            throw new ArgumentException("FSD host version is required.", nameof(hostVersion));

        FsdExecutionLease? lease = null;
        FsdEngine? module = null;
        try
        {
            pluginInterface.Create<Service>(Array.Empty<object>());
            _hostIdentity = hostIdentity;
            _hostVersion = hostVersion;
            _detailedMapHostOptions = detailedMapHostOptions;
            _settings = settingsStore.Load()
                ?? throw new InvalidOperationException("The FSD settings store returned null.");
            _settings.AttachStore(settingsStore);
            _settings.Rotation ??= new();
            _settings.Rotation.NormalizeProvider();
            FsdSettingsValidator.ValidateOrThrow(_settings);
            lease = new FsdExecutionLease($"{hostIdentity}/{hostVersion}");
            _lease = lease;
            _playerYOffsetProvider = playerYOffsetProvider;
            CombatPositionNavigation.SetYOffsetProvider(playerYOffsetProvider);
            module = new FsdEngine(
                _settings,
                _encounters,
                _lease,
                hostIdentity,
                hostVersion,
                pluginInterface.GetPluginConfigDirectory(),
                detailedMapHostOptions,
                floorEvidenceObserver,
                runTelemetryObserver,
                tryAuthorizeFsdStart,
                fsdStartDenialNoticeProvider,
                () => new SpinBossMovementStrategy(_spin),
                stateObserver);
            _module = module;
            _module.Initialize();
            Service.Log.Info($"[FSD] Host {hostIdentity}/{hostVersion}; engine {FsdEngineIdentity.InformationalVersion}.");
            Service.Log.Info($"[FSD] builds engineMvid={typeof(FsdApplication).Assembly.ManifestModule.ModuleVersionId}; coreMvid={typeof(DeepDungeon.Fsd.Core.NativeTreasureCompletionPlanner).Assembly.ManifestModule.ModuleVersionId}; runtimeMvid={typeof(DeepDungeon.Fsd.Runtime.FsdRotationController).Assembly.ManifestModule.ModuleVersionId}; mechanicMvid={typeof(MasterPlayer.MechanicRuntime.EncounterHintPlanner).Assembly.ManifestModule.ModuleVersionId}");
        }
        catch (Exception startupError)
        {
            var rollbackErrors = new List<Exception>();
            try { CombatPositionNavigation.ClearYOffsetProvider(playerYOffsetProvider); } catch (Exception error) { rollbackErrors.Add(error); }
            try { module?.Dispose(); } catch (Exception error) { rollbackErrors.Add(error); }
            try { _spin.Dispose(); } catch (Exception error) { rollbackErrors.Add(error); }
            try { lease?.Dispose(); } catch (Exception error) { rollbackErrors.Add(error); }

            if (rollbackErrors.Count == 0)
                throw;

            rollbackErrors.Insert(0, startupError);
            throw new AggregateException("FSD facade startup and rollback both failed.", rollbackErrors);
        }
    }

    /// <summary>Complete standalone composition. This application owns its host.</summary>
    public static FsdApplication CreateStandalone(
        IDalamudPluginInterface pluginInterface, IFsdSettingsStore settingsStore,
        string hostIdentity, string hostVersion, DetailedMapHostOptions detailedMapHostOptions,
        Func<float> playerYOffsetProvider,
        IFloorEvidenceObserver? floorEvidenceObserver = null,
        IRunTelemetryObserver? runTelemetryObserver = null,
        FsdStartAuthorizationCallback? tryAuthorizeFsdStart = null,
        Func<string?>? fsdStartDenialNoticeProvider = null,
        Action<DeepDungeonStateSnapshot>? stateObserver = null)
    {
        var host = new EncounterMechanicHost(pluginInterface);
        try
        {
            var app = new FsdApplication(pluginInterface, settingsStore, hostIdentity, hostVersion,
                detailedMapHostOptions, host, playerYOffsetProvider, floorEvidenceObserver, runTelemetryObserver,
                tryAuthorizeFsdStart, fsdStartDenialNoticeProvider, stateObserver);
            app._ownedEncounter = host;
            return app;
        }
        catch { host.Dispose(); throw; }
    }

    public FsdSettings Settings => _settings;
    public bool IsRunActive => _module.IsRunActive;
    public string? ActiveDetailedMapReleaseId =>
        _module.ActiveDetailedMapReleaseId;
    public DeepDungeonStateSnapshot CurrentDeepDungeonState => _module.CurrentDeepDungeonState;

    public FsdControlResult Start(FsdStartRequest? request = null)
    {
        ThrowIfDisposed();
        return _module.StartSelectedFarming(request ?? new FsdStartRequest());
    }

    public FsdControlResult Stop()
    {
        ThrowIfDisposed();
        return _module.StopDeepDungeonFsd();
    }

    public void Update(IFramework framework)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(framework);
        try
        {
            _settings.Refresh();
            FsdSettingsValidator.ValidateOrThrow(_settings);
            if (_ownedEncounter is { } owned)
            {
                if (IsRunActive) owned.Update(framework);
                else if (owned.IsAvailable || owned.LastError != null) owned.Reset();
            }
            _spin.Update();
            _module.Update(framework);
            _losDebug?.Update();
        }
        catch
        {
            _module.SuspendBossMovement();
            throw;
        }
    }

    public void Update() => Update(Service.Framework);

    public void SuspendBossMovement() { ThrowIfDisposed(); _module.SuspendBossMovement(); }

    public void Draw()
    {
        ThrowIfDisposed();
        _module.DrawDeepDungeonFormalPanel();
    }

    public FsdApplicationSnapshot Snapshot()
    {
        ThrowIfDisposed();
        return new FsdApplicationSnapshot(
            _hostIdentity,
            _hostVersion,
            FsdEngineIdentity.InformationalVersion,
            _lease.IsHeld,
            _module.GetMobPilotSnapshot());
    }

    public void DrawOverlay()
    {
        try { _module?.DrawSitePoolOverlay(); }
        catch (Exception ex) { Service.Log.Error($"[FSD] site-pool overlay: {ex}"); }
        _losDebug?.Draw();
        _ownedEncounter?.DrawOverlay();
    }

    public void DrawDeepDungeonFormalPanel() { ThrowIfDisposed(); _module.DrawDeepDungeonFormalPanel(); }
    public void DrawGeneralAssistantSettings() { ThrowIfDisposed(); _module.DrawGeneralAssistantSettings(); }
    public void DrawCompanionSettings() { ThrowIfDisposed(); _module.DrawCompanionSettings(); }
    public void DrawDeepDungeonDebugPanel() { ThrowIfDisposed(); _module.DrawDeepDungeonDebugPanel(); }
    public object GetMobPilotSnapshot() { ThrowIfDisposed(); return _module.GetMobPilotSnapshot(); }
    public object GetPilgrimsTraverseFsdPreflight(int startFloor) { ThrowIfDisposed(); return _module.GetPilgrimsTraverseFsdPreflight(startFloor); }
    public object StartPilgrimsTraverseFsd(int startFloor, int targetLoops, bool infinite, string? confirmation, string? leaveModeOverride = null) { ThrowIfDisposed(); return _module.StartPilgrimsTraverseFsd(startFloor, targetLoops, infinite, confirmation, leaveModeOverride); }

    public FsdControlResult StartFarming(FsdFarmingRequest request)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(request);
        bool started = _module.TryStartFarming(request, out string error);
        return new FsdControlResult(started, error);
    }

    public FsdControlResult StopDeepDungeonFsd() => Stop();
    public object StartDeepDungeonLeaveDuty(string? confirmation) { ThrowIfDisposed(); return _module.StartDeepDungeonLeaveDuty(confirmation); }
    public object CloseDeepDungeonEntryWindowsForBridge() { ThrowIfDisposed(); return _module.CloseDeepDungeonEntryWindowsForBridge(); }
    public object StartPilgrimsTraverseDeleteSaveSlot(int slotNumber, string? confirmation) { ThrowIfDisposed(); return _module.StartPilgrimsTraverseDeleteSaveSlot(slotNumber, confirmation); }
    public void Dispose()
    {
        if (!_lifecycle.TryDispose())
            return;
        CombatPositionNavigation.ClearYOffsetProvider(_playerYOffsetProvider);
        try { _module.Dispose(); }
        finally
        {
            try { _spin.Dispose(); }
            finally
            {
                try { _ownedEncounter?.Dispose(); }
                finally { _lease.Dispose(); }
            }
        }
    }

    private void ThrowIfDisposed() => _lifecycle.EnsureActive();
}
