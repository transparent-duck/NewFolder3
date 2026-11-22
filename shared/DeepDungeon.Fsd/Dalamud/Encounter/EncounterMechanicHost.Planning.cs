using MasterPlayer.MechanicRuntime;

namespace DeepDungeon.Fsd.Dalamud.Encounter;

// Planning bridge: one shared AIHints adapter, published only after a
// successful hint rebuild and consumed on the framework thread.
public sealed partial class EncounterMechanicHost
{
    private EncounterHintSnapshotBuilder? _planningBuilder;
    private EncounterPlanningSnapshot? _encounterSnapshot;

    /// <summary>Latest frozen planning view — null while the encounter
    /// state is unavailable or the player is logged out.</summary>
    public EncounterPlanningSnapshot? GetEncounterSnapshot() => _encounterSnapshot;

    /// <summary>Called from Update after _session.Update().</summary>
    private void UpdatePlanningSnapshot()
    {
        if (_session == null)
        {
            _planningBuilder?.Reset();
            _encounterSnapshot = null;
            return;
        }
        // Logout guard — no player means no live encounter state.
        if (_session.World.Party.Player() == null)
        {
            _planningBuilder?.Reset();
            _encounterSnapshot = null;
            return;
        }
        _planningBuilder ??= new EncounterHintSnapshotBuilder();
        _encounterSnapshot = _planningBuilder.Capture(_session, _aiHints);
    }

    /// <summary>Called from Dispose — drops the builder and snapshot.</summary>
    private void ResetPlanningSnapshot()
    {
        _planningBuilder?.Reset();
        _encounterSnapshot = null;
    }
}
