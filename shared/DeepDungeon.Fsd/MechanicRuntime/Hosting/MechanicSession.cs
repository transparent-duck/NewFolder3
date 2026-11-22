using BossMod;

namespace MasterPlayer.MechanicRuntime;

public sealed class MechanicSession : IDisposable
{
    public WorldState World { get; }
    public BossModuleManager BossModules { get; }
    public ZoneModuleManager ZoneModules { get; }
    public DisplacementTimingTracker Displacements { get; } = new();
    private readonly EncounterDutyAdapters _dutyAdapters;
    private readonly EventSubscriptions _healthSubscriptions;
    private readonly Dictionary<ulong, string> _moduleFailures = new();
    public ExecutionRestrictions GetExecutionRestrictions(Actor player) => _dutyAdapters.GetRestrictions(player);
    private bool _disposed;

    public MechanicSession(string gameVersion)
    {
        if (!MechanicRuntimeEnvironment.IsInitialized)
            throw new InvalidOperationException("Initialize BMR game data and configuration before creating a session.");
        World = new((ulong)TimeSpan.TicksPerSecond, gameVersion);
        BossModules = new(World);
        _healthSubscriptions = new(
            BossModules.ModuleUpdateFailed.Subscribe((module, error) =>
                _moduleFailures[module.PrimaryActor.InstanceID] = $"{module.GetType().Name}: {error.Message}"),
            BossModules.ModuleLoaded.Subscribe(module => _moduleFailures.Remove(module.PrimaryActor.InstanceID)),
            World.CurrentZoneChanged.Subscribe(_ => _moduleFailures.Clear()));
        ZoneModules = new(World);
        _dutyAdapters = new(this);
    }

    public void UpdateHints(AIHintsBuilder? builder, AIHints hints, int slot, bool moveImminent)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Update(hints, slot, moveImminent);
    }

    public EncounterReadiness GetReadiness(ulong actorId, uint oid, DateTime hintsAt, DateTime now)
    {
        if (World.CurrentTime == default || now - World.CurrentTime > TimeSpan.FromSeconds(.5))
            return new(EncounterReadinessKind.Faulted, "Encounter world observations are stale.", World.CurrentTime);
        if (World.Party.Player() == null)
            return new(EncounterReadinessKind.Waiting, "Waiting for the local player observation.", World.CurrentTime);
        if (actorId == 0)
            return new(EncounterReadinessKind.Waiting, "Waiting for the boss object.", World.CurrentTime);
        var actor = World.Actors.Find(actorId);
        if (actor == null || actor.OID != oid)
            return new(EncounterReadinessKind.Faulted, "The live boss is missing from the encounter world.", World.CurrentTime);
        if (actor.IsDead)
            return new(EncounterReadinessKind.Ready, "Boss death observed.", World.CurrentTime);
        if (_moduleFailures.TryGetValue(actorId, out var error))
            return new(EncounterReadinessKind.Faulted, $"Boss module update failed: {error}", World.CurrentTime);
        var info = BossModuleRegistry.FindByOID(oid);
        if (info == null || info.Maturity < BossModuleManager.Config.MinMaturity)
            return new(EncounterReadinessKind.Unsupported, $"No enabled BMR module for boss OID 0x{oid:X}.", World.CurrentTime);
        if (BossModules.ActiveModule?.PrimaryActor.InstanceID != actorId)
        {
            foreach (var module in BossModules.PendingModules)
                if (module.PrimaryActor.InstanceID == actorId)
                    return new(EncounterReadinessKind.Approach, "Approaching the supported boss module's load range.", World.CurrentTime);
            return new(EncounterReadinessKind.Faulted, $"Expected BMR module {info.ModuleType.Name} is not active for this boss.", World.CurrentTime);
        }
        if (hintsAt == default || now - hintsAt > TimeSpan.FromSeconds(.5))
            return new(EncounterReadinessKind.Faulted, "Encounter hint publication is stale.", hintsAt);
        return new(EncounterReadinessKind.Ready, info.ModuleType.Name, hintsAt);
    }

    public void BeginFrame(DateTime timestamp)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var previous = World.CurrentTime;
        if (timestamp == default || (previous != default && timestamp < previous))
            throw new ArgumentOutOfRangeException(nameof(timestamp), "Seeking backwards requires a new world session.");
        var delta = previous == default ? 0 : (float)(timestamp - previous).TotalSeconds;
        World.Execute(new WorldState.OpFrameStart(new(timestamp, (ulong)timestamp.Ticks,
            World.Frame.Index + 1, delta, delta, 1), default, World.Client.GaugePayload, World.Client.CameraAzimuth));
    }

    public void Execute(WorldState.Operation operation)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        World.Execute(operation);
    }

    public void Update()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ZoneModules.ActiveModule?.Update();
        BossModules.Update();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _healthSubscriptions.Dispose();
        _dutyAdapters.Dispose();
        List<Exception> errors = [];
        try { ZoneModules.Dispose(); }
        catch (Exception ex) { errors.Add(ex); }
        finally { ZoneModules.ActiveModule = null; }
        try { BossModules.Dispose(); }
        catch (Exception ex) { errors.Add(ex); }
        if (errors.Count != 0)
            throw new AggregateException("BMR session cleanup failed.", errors);
    }
}
