using System.Numerics;

namespace MasterPlayer.MechanicRuntime;

/// <summary>Attack range must hold at both supported action origins.</summary>
public static class AttackRegionGoal
{
    public static NavigationRegionGoal Create(Func<INavigationGrid, Vector3, Vector3?> ground,
        Func<Vector3, Vector3, float, float, bool?> checkAttack) =>
        new(ground, checkAttack, HorizontalRadiusSquared);

    private static float HorizontalRadiusSquared(Vector3 player, Vector3 target, float range)
    {
        var dy = player.Y - target.Y;
        return range * range - Math.Max(dy * dy, (dy - 7) * (dy - 7));
    }
}
