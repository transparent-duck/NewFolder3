using BossMod;

namespace MasterPlayer.MechanicRuntime;

[Flags]
public enum ExecutionRestrictions
{
    None = 0,
    Movement = 1,
    Actions = 2,
    CancelCast = 4,
    StopAll = Movement | Actions | CancelCast
}

/// <summary>One framework-frame result shared by navigation, rotation and action dispatch.</summary>
public static class MechanicExecutionState
{
    public static ExecutionRestrictions Restrictions { get; private set; }
    public static bool MovementBlocked => (Restrictions & ExecutionRestrictions.Movement) != 0;
    public static bool ActionsBlocked => (Restrictions & ExecutionRestrictions.Actions) != 0;
    public static void Publish(ExecutionRestrictions restrictions) => Restrictions = restrictions;

    public static ExecutionRestrictions ForStatus(uint id) => id switch
    {
        960 => ExecutionRestrictions.StopAll, // Pyretic: movement and all actions, including items.
        _ => ExecutionRestrictions.None
    };

    public static ExecutionRestrictions FromHints(AIHints hints, DateTime now)
    {
        var result = hints.ForceCancelCast ? ExecutionRestrictions.CancelCast : ExecutionRestrictions.None;
        var mode = hints.ImminentSpecialMode;
        if (now.AddSeconds(EncounterHintPlanner.ImpactLeadSeconds) >= mode.activation
            && (mode.finish == default || now <= mode.finish))
            result |= mode.mode switch
            {
                AIHints.SpecialMode.Pyretic => ExecutionRestrictions.StopAll,
                AIHints.SpecialMode.NoMovement => ExecutionRestrictions.Movement,
                _ => ExecutionRestrictions.None
            };
        return result;
    }
}
