using System;
using MasterPlayer.MechanicRuntime;

namespace DeepDungeon.Fsd.Dalamud.Encounter;

public sealed partial class EncounterMechanicHost
{
    private MechanicExecutionController? _execution;

    private void UpdateExecutionRestrictions()
    {
        var restrictions = ExecutionRestrictions.None;
        var now = System.DateTime.UtcNow;
        if (_session?.World.Party.Player() is { IsDead: false } player)
        {
            // This runs every framework frame, independently of boss snapshots
            // and the geometry-hint rebuild interval. Status presence is authoritative.
            foreach (ref readonly var status in player.Statuses.AsSpan())
                restrictions |= MechanicExecutionState.ForStatus(status.ID);
            restrictions |= _session.GetExecutionRestrictions(player);
            restrictions |= MechanicExecutionState.FromHints(_aiHints, now);
        }
        _execution ??= new(this);
        _execution.Update(restrictions, now);
    }
}
