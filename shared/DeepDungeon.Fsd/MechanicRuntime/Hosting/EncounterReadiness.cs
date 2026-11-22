namespace MasterPlayer.MechanicRuntime;

public enum EncounterReadinessKind { Waiting, Approach, Ready, Unsupported, Faulted }

/// <summary>Availability of the data source, not a judgement about route safety
/// or whether a module implements every mechanic.</summary>
public readonly record struct EncounterReadiness(EncounterReadinessKind Kind, string Detail,
    DateTime ObservedAt = default)
{
    public bool Failed => Kind is EncounterReadinessKind.Faulted or EncounterReadinessKind.Unsupported;
}
