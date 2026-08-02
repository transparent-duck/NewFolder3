using global::Dalamud.Plugin;
using global::Dalamud.Plugin.Services;
using DeepDungeon.Fsd.Core;
using DeepDungeon.Fsd.Dalamud.Runtime;

namespace DeepDungeon.Fsd.Dalamud;

public sealed class FsdApplication : IFsdApplication
{
    private readonly string _hostIdentity;
    private readonly string _hostVersion;
    private readonly DetailedMapHostOptions _detailedMapHostOptions;
    private readonly FsdSettings _settings;
    private readonly FsdExecutionLease _lease;
    private readonly FsdEngine _module;
    private readonly FsdFacadeLifecycle _lifecycle = new();

    public FsdApplication(
        IDalamudPluginInterface pluginInterface,
        IFsdSettingsStore settingsStore,
        string hostIdentity,
        string hostVersion,
        DetailedMapHostOptions detailedMapHostOptions,
        IFloorEvidenceObserver? floorEvidenceObserver = null,
        IRunTelemetryObserver? runTelemetryObserver = null,
        FsdStartAuthorizationCallback? tryAuthorizeFsdStart = null)
    {
        ArgumentNullException.ThrowIfNull(pluginInterface);
        ArgumentNullException.ThrowIfNull(settingsStore);
        ArgumentNullException.ThrowIfNull(detailedMapHostOptions);
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
            FsdSettingsValidator.ValidateOrThrow(_settings);
            lease = new FsdExecutionLease($"{hostIdentity}/{hostVersion}");
            _lease = lease;
            module = new FsdEngine(
                _settings,
                _lease,
                hostIdentity,
                hostVersion,
                pluginInterface.GetPluginConfigDirectory(),
                detailedMapHostOptions,
                floorEvidenceObserver,
                runTelemetryObserver,
                tryAuthorizeFsdStart);
            _module = module;
            _module.Initialize();
            Service.Log.Info($"[FSD] Host {hostIdentity}/{hostVersion}; engine {FsdEngineIdentity.InformationalVersion}.");
        }
        catch (Exception startupError)
        {
            var rollbackErrors = new List<Exception>();
            try { module?.Dispose(); } catch (Exception error) { rollbackErrors.Add(error); }
            try { lease?.Dispose(); } catch (Exception error) { rollbackErrors.Add(error); }

            if (rollbackErrors.Count == 0)
                throw;

            rollbackErrors.Insert(0, startupError);
            throw new AggregateException("FSD facade startup and rollback both failed.", rollbackErrors);
        }
    }

    public FsdSettings Settings => _settings;
    public bool IsRunActive => _module.IsRunActive;
    public bool SupportsControlledPtSurvey => _detailedMapHostOptions.SupportsControlledPtSurvey;
    public string? ActiveDetailedMapReleaseId =>
        _module.ActiveDetailedMapReleaseId;
    public DeepDungeonStateSnapshot CurrentDeepDungeonState => _module.CurrentDeepDungeonState;

    public object Start()
    {
        ThrowIfDisposed();
        if (_settings.NecromancerFsdScenarioIndex == 2)
        {
            if (!_detailedMapHostOptions.SupportsControlledPtSurvey)
            {
                return new
                {
                    ok = false,
                    error = "Controlled reusable-save survey capture is unavailable for this FSD host."
                };
            }

            return StartControlledPilgrimsTraverseCapture(
                Math.Max(1, _settings.NecromancerFsdLoopCount),
                _settings.NecromancerFsdLoopInfinite,
                "start-controlled-pt-capture");
        }
        var floor = _settings.NecromancerFsdScenarioIndex == 0 ? 21 : 31;
        return StartPilgrimsTraverseFsd(
            floor,
            Math.Max(1, _settings.NecromancerFsdLoopCount),
            _settings.NecromancerFsdLoopInfinite,
            "start-pt-fsd");
    }

    public object Stop()
    {
        ThrowIfDisposed();
        return StopDeepDungeonFsd();
    }

    public void Update(IFramework framework)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(framework);
        _settings.Refresh();
        FsdSettingsValidator.ValidateOrThrow(_settings);
        _module.Update(framework);
    }

    public void Update() => Update(Service.Framework);

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

    public void DrawDeepDungeonFormalPanel() { ThrowIfDisposed(); _module.DrawDeepDungeonFormalPanel(); }
    public void DrawGeneralAssistantSettings() { ThrowIfDisposed(); _module.DrawGeneralAssistantSettings(); }
    public void DrawDeepDungeonDebugPanel() { ThrowIfDisposed(); _module.DrawDeepDungeonDebugPanel(); }
    public object GetMobPilotSnapshot() { ThrowIfDisposed(); return _module.GetMobPilotSnapshot(); }
    public object GetPilgrimsTraverseFsdPreflight(int startFloor) { ThrowIfDisposed(); return _module.GetPilgrimsTraverseFsdPreflight(startFloor); }
    public object StartPilgrimsTraverseFsd(int startFloor, int targetLoops, bool infinite, string? confirmation, string? leaveModeOverride = null) { ThrowIfDisposed(); return _module.StartPilgrimsTraverseFsd(startFloor, targetLoops, infinite, confirmation, leaveModeOverride); }
    public object StopDeepDungeonFsd() { ThrowIfDisposed(); return _module.StopDeepDungeonFsd(); }
    public object StartDeepDungeonLeaveDuty(string? confirmation) { ThrowIfDisposed(); return _module.StartDeepDungeonLeaveDuty(confirmation); }
    public object CloseDeepDungeonEntryWindowsForBridge() { ThrowIfDisposed(); return _module.CloseDeepDungeonEntryWindowsForBridge(); }
    public object StartPilgrimsTraverseDeleteSaveSlot(int slotNumber, string? confirmation) { ThrowIfDisposed(); return _module.StartPilgrimsTraverseDeleteSaveSlot(slotNumber, confirmation); }
    public object ArmControlledReusableSaveSurveyCapture()
    {
        ThrowIfDisposed();
        if (!_detailedMapHostOptions.SupportsControlledPtSurvey)
        {
            return new
            {
                ok = false,
                error = "Controlled reusable-save survey capture is unavailable for this FSD host."
            };
        }

        return _module.ArmControlledReusableSaveSurveyCapture();
    }
    public object StartControlledPilgrimsTraverseCapture(int targetLoops, bool infinite, string? confirmation)
    {
        ThrowIfDisposed();
        if (!_detailedMapHostOptions.SupportsControlledPtSurvey)
        {
            return new
            {
                ok = false,
                error = "Controlled reusable-save survey capture is unavailable for this FSD host."
            };
        }

        return _module.StartControlledPilgrimsTraverseCapture(targetLoops, infinite, confirmation);
    }

    public void Dispose()
    {
        if (!_lifecycle.TryDispose())
            return;
        _module.Dispose();
        _lease.Dispose();
    }

    private void ThrowIfDisposed() => _lifecycle.EnsureActive();
}
