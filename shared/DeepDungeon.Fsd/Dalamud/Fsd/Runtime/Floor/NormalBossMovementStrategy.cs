using FFXIVClientStructs.FFXIV.Client.Game.InstanceContent;
using MasterPlayer.MechanicRuntime;
using Dalamud.Game.ClientState.Conditions;

namespace DeepDungeon.Fsd.Dalamud.Runtime.Floor;

/// <summary>Mechanic navigation with an explicit supported-module approach phase.</summary>
internal sealed unsafe class NormalBossMovementStrategy(FloorExplorationController floor) : IBossMovementStrategy
{
    public string Name => "mechanics";
    public bool SuppressCombatItems => false;
    public bool RequestsNorthCamera => false;
    public void Start() { }

    public string? Update(InstanceContentDeepDungeon* dungeon)
    {
        var readiness = floor.GetBossReadiness();
        if (readiness.Kind == EncounterReadinessKind.Approach && Service.Condition[ConditionFlag.InCombat])
            readiness = new(EncounterReadinessKind.Faulted,
                "Combat started before the supported boss module became ready.", readiness.ObservedAt);
        if (readiness.Kind == EncounterReadinessKind.Ready)
        {
            if (floor.UpdateBossEncounter(dungeon))
            {
                floor.Boss.ReportMechanicReadiness(readiness);
                return null;
            }
            readiness = new(EncounterReadinessKind.Faulted,
                "Mechanic coordination is unavailable.", readiness.ObservedAt);
        }
        floor.Boss.ReportMechanicReadiness(readiness);
        if (readiness.Kind == EncounterReadinessKind.Approach)
        {
            // A known pending module can load as we approach. This is not a
            // replacement for missing/failed mechanics after combat starts.
            floor.Boss.UpdateApproach(dungeon);
            return "Boss floor - approaching supported module load range";
        }
        floor.CancelCoordMovement();
        return $"Boss floor - {readiness.Kind.ToString().ToLowerInvariant()}: {readiness.Detail}";
    }

    public void Stop() => floor.CancelCoordMovement();
}
