using BossMod;

namespace MasterPlayer.MechanicRuntime;

// A frozen group of candidate stack positions at one resolution. Members of a
// party never move with a simulated marker unless they are the controlled actor.
public sealed class StackPositionGoal(WPos[] members, float radius, int maximum,
    DateTime activation, bool holding)
{
    public DateTime Activation => activation;
    // Among legal positions, an extra covered ally always outranks a margin
    // improvement. Inside a common coverage tier prefer its most central point.
    public float Cost(in WPos point)
    {
        // A moving marker can change position between the final plan and resolution.
        // Settle in the inner half of its circle without chasing the exact centre.
        if (!holding) return Math.Max(0, (point - members[0]).Length() - radius * .5f);
        var count = 1;
        var farthest = 0f;
        var nearest = float.PositiveInfinity;
        foreach (var member in members)
        {
            var d = (point - member).LengthSq();
            nearest = Math.Min(nearest, d);
            if (d > radius * radius) continue;
            ++count;
            farthest = Math.Max(farthest, d);
        }
        var desired = Math.Min(maximum, members.Length + 1);
        var miss = Math.Abs(desired - count);
        return miss * (radius + 1) + (count > 1 ? Math.Max(0, MathF.Sqrt(farthest) - Math.Max(0, radius - 0.65f)) :
            radius * MathF.Sqrt(nearest) / (radius + MathF.Sqrt(nearest)));
    }
}
