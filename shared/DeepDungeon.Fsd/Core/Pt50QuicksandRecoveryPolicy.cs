using System.Numerics;

namespace DeepDungeon.Fsd.Core;

public static class Pt50QuicksandRecoveryPolicy
{
    // Interiors of BMR CN's seven collision-derived rock polygons (DD50Ogbunabali).
    private static readonly Vector2[] Rocks = [new(-288.223f, -300.977f), new(-293.132f, -293.073f),
        new(-302.569f, -290.024f), new(-308.233f, -298.925f), new(-308.568f, -307.865f),
        new(-300.241f, -305.082f), new(-294.132f, -309.576f)];
    public static ReadOnlySpan<Vector2> RockInteriors => Rocks;

    public static bool ShouldRecover(float sinkingRemaining, float? windRemaining) =>
        sinkingRemaining > 0 && sinkingRemaining <= 2.8f &&
        !(windRemaining is > 0 and <= 2f && sinkingRemaining > windRemaining.Value + 0.5f);

    public static Vector2 NearestRock(Vector2 position, byte allowedMask = 0x7f)
    {
        // If no hazard-safe rock is reachable, the nearest solid ground is the last resort.
        if (allowedMask == 0) allowedMask = 0x7f;
        float distance = float.MaxValue;
        Vector2 best = Rocks[0];
        for (int i = 0; i < Rocks.Length; i++)
        {
            if ((allowedMask & (1 << i)) == 0) continue;
            float candidate = Vector2.DistanceSquared(position, Rocks[i]);
            if (candidate < distance) { distance = candidate; best = Rocks[i]; }
        }
        return best;
    }
}
