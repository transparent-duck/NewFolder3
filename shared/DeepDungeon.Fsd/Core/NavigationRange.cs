using System.Numerics;

namespace DeepDungeon.Fsd.Core;

public enum NavigationDistanceMetric { Horizontal, Spatial }

/// <summary>
/// An allowed target region, separate from the shared movement tolerance.
/// Approach and candidate selection reserve room for that tolerance; actual
/// interaction checks always use the original region and the same metric.
/// </summary>
public readonly record struct NavigationRange(float Radius,
    NavigationDistanceMetric Metric = NavigationDistanceMetric.Horizontal)
{
    public const float MovementTolerance = 0.1f;
    public static readonly NavigationRange Point = new(MovementTolerance);

    public float StandingRadius => Math.Max(0, Radius - MovementTolerance);
    // A point request uses the movement tolerance itself. Larger regions
    // reserve the tolerance once, regardless of how many layers test arrival.
    public float StoppingRadius => Radius <= MovementTolerance ? Radius : StandingRadius;

    public float DistanceSquared(Vector3 position, Vector3 target)
    {
        var delta = position - target;
        return delta.X * delta.X + delta.Z * delta.Z
            + (Metric == NavigationDistanceMetric.Spatial ? delta.Y * delta.Y : 0);
    }

    public float Distance(Vector3 position, Vector3 target) => MathF.Sqrt(DistanceSquared(position, target));
    public bool Contains(Vector3 position, Vector3 target) => Within(position, target, Radius);
    public bool HasArrived(Vector3 position, Vector3 target) => Within(position, target, StoppingRadius);
    public bool CanStandAt(Vector3 position, Vector3 target) => Within(position, target, StandingRadius);

    private bool Within(Vector3 position, Vector3 target, float radius) =>
        float.IsFinite(Radius) && Radius >= 0 && float.IsFinite(radius)
        && float.IsFinite(position.X) && float.IsFinite(position.Y) && float.IsFinite(position.Z)
        && float.IsFinite(target.X) && float.IsFinite(target.Y) && float.IsFinite(target.Z)
        && DistanceSquared(position, target) <= radius * radius;
}
