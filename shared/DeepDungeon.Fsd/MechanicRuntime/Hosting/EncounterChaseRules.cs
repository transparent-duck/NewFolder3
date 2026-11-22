using BossMod;

namespace MasterPlayer.MechanicRuntime;

internal static class EncounterChaseRules
{
    internal static WPos Landing(WPos previous, WPos target, float step)
    {
        var delta = target - previous;
        var distance = delta.Length();
        return (distance > step && distance > 0 ? previous + delta * (step / distance) : target).Quantized();
    }

    internal static float Radius(float radius, float speed) =>
        radius + speed * (float)EncounterHintPlanner.ImpactLeadSeconds + EncounterHintPlanner.HintMargin;
}
