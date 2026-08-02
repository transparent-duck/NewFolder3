using System.Collections.Generic;
using System.Numerics;
using FFXIVClientStructs.FFXIV.Client.System.Framework;
using FFXIVClientStructs.FFXIV.Common.Component.BGCollision;
using FFXIVClientStructs.FFXIV.Common.Component.BGCollision.Math;

namespace DeepDungeon.Fsd.Dalamud.Map;

internal readonly struct CollisionPoint
{
    public Vector3 Center { get; init; }
    public float BoundsMinY { get; init; }
    public float BoundsMaxY { get; init; }
    public Vector2 XZ => new(Center.X, Center.Z);
}

/// <summary>
/// Simple helper that scans BGCollision scenes for analytic shapes with the
/// default forbidden material and returns their world-space centers.
/// </summary>
internal static unsafe class BgCollisionShapeFinder
{
    private const ulong MaterialValue = 0x6400;
    private const ulong MaterialMask = 0x1FFFFFFFFF;

    /// <summary>
    /// Enumerate all analytic colliders whose material matches (value, mask)
    /// and return their centers and AABB Y bounds in world space.
    /// </summary>
    public static List<CollisionPoint> GetFilteredCenters()
    {
        var results = new List<CollisionPoint>();

        var framework = Framework.Instance();
        if (framework == null)
            return results;

        var module = framework->BGCollisionModule;
        if (module == null)
            return results;

        var sceneManager = module->SceneManager;
        if (sceneManager == null)
            return results;

        foreach (var sceneWrapper in sceneManager->Scenes)
        {
            var scene = sceneWrapper->Scene;
            if (scene == null)
                continue;

            foreach (var collider in scene->Colliders)
            {
                if (collider == null)
                    continue;
                if (!MatchesMaterial(collider))
                    continue;
                if (TryGetCollisionPoint(collider, out var point))
                    results.Add(point);
            }
        }

        return results;
    }

    private static bool MatchesMaterial(Collider* collider)
    {
        var material = collider->ObjectMaterialValue;
        return ((material ^ MaterialValue) & MaterialMask) == 0;
    }

    private static bool TryGetCollisionPoint(Collider* collider, out CollisionPoint point)
    {
        point = default;
        var type = collider->GetColliderType();
        Vector3 center;
        switch (type)
        {
            case ColliderType.Box:
                center = GetMatrixTranslation(((ColliderBox*)collider)->World);
                break;
            case ColliderType.Cylinder:
                center = GetMatrixTranslation(((ColliderCylinder*)collider)->World);
                break;
            case ColliderType.Sphere:
                center = GetMatrixTranslation(((ColliderSphere*)collider)->World);
                break;
            case ColliderType.Plane:
            case ColliderType.PlaneTwoSided:
                center = GetMatrixTranslation(((ColliderPlane*)collider)->World);
                break;
            default:
                return false;
        }

        AABB bounds;
        collider->GetWorldBB(&bounds);
        point = new CollisionPoint
        {
            Center = center,
            BoundsMinY = bounds.Min.Y,
            BoundsMaxY = bounds.Max.Y,
        };
        return true;
    }

    private static Vector3 GetMatrixTranslation(Matrix4x3 matrix) => matrix.Row3;
}

