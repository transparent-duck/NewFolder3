namespace MasterPlayer.MechanicRuntime;

/// <summary>A coherent framework-thread view. Reading never pumps native input
/// or advances the world; the source owner updates before its consumers.</summary>
public readonly record struct EncounterFrame(EncounterPlanningSnapshot? Encounter,
    EncounterPlanningSnapshot? LocalHazards, EncounterReadiness Readiness,
    ExecutionRestrictions Restrictions, DateTime ObservedAt);

public interface IEncounterSource
{
    EncounterFrame Read(ulong actorId = 0, uint oid = 0);
}
