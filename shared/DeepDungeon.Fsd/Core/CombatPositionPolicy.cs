using System.Numerics;

namespace DeepDungeon.Fsd.Core;

/// <summary>Fixed pursuit hysteresis. Short-range actions retain their own range.</summary>
public readonly record struct CombatChaseRanges(float Approach, float Hold)
{
    public static CombatChaseRanges ForAction(float range) =>
        float.IsFinite(range) && range > 0
            ? new(Math.Min(15f, Math.Max(.1f, range - .5f)), Math.Min(24f, range))
            : new(15f, 24f);
}

/// <summary>Shared action range and execution timing; search has no radius or path-count limits.</summary>
public static class CombatPositionPolicy
{
    public static float DefaultAttackRange(uint job) => job switch
    {
        0 or 1 or 2 or 3 or 4 or 19 or 20 or 21 or 22 or 29 or 30 or 32 or 34 or 37 or 39 or 41 => 3f,
        _ => 25f
    };

    public static readonly TimeSpan AttemptLimit = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan UnknownNoProgressLimit = TimeSpan.FromSeconds(15);

    public static bool IsFinite(Vector3 point) =>
        float.IsFinite(point.X) && float.IsFinite(point.Y) && float.IsFinite(point.Z);

    // Action range is the straight-line distance between centres in 3D.
    public static bool WithinRange(Vector3 position, Vector3 target, float actionRange,
        float yOffset, float margin)
    {
        if (!IsFinite(position) || !IsFinite(target) || !float.IsFinite(actionRange) || actionRange <= 0)
            return false;
        float range = actionRange - margin;
        var origin = position + new Vector3(0, yOffset, 0);
        return float.IsFinite(range) && range > 0 && Vector3.DistanceSquared(origin, target) <= range * range;
    }


}
