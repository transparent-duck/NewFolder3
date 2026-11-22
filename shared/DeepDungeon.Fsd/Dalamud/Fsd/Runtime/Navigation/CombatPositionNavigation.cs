using MasterPlayer.MechanicRuntime;
using System.Numerics;
using System.Runtime.InteropServices;
using DeepDungeon.Fsd.Core;
using DeepDungeon.Fsd.Dalamud.Coord;
using FFXIVClientStructs.FFXIV.Client.System.Framework;
using FFXIVClientStructs.FFXIV.Common.Component.BGCollision;

namespace DeepDungeon.Fsd.Dalamud.Runtime.Navigation;

internal enum CombatSight { Unknown, Clear, Blocked }

internal readonly record struct CombatSightSample(Vector3 Origin, Vector3 OffsetOrigin, Vector3 End,
    CombatSight Normal, CombatSight Offset, Vector3 NormalHit, Vector3 OffsetHit, float YOffset = 0f)
{
    public bool HasOffset => CombatPositionNavigation.HasYOffset(YOffset);
    public bool Blocked => Normal == CombatSight.Blocked || (HasOffset && Offset == CombatSight.Blocked);
    public bool Clear => Normal == CombatSight.Clear && (!HasOffset || Offset == CombatSight.Clear);
}

internal readonly record struct CombatAttackCheck(bool InRange, CombatSight Sight, CombatSight OffsetSight,
    Vector3 HitPoint, Vector3 OffsetHitPoint, bool HasOffset = false)
{
    public bool Allowed => InRange && Sight == CombatSight.Clear && (!HasOffset || OffsetSight == CombatSight.Clear);
    public bool SightBlocked => Sight == CombatSight.Blocked || (HasOffset && OffsetSight == CombatSight.Blocked);
    // Missing collision observations are not evidence of an obstruction.
    public bool? AvailableResult => Allowed ? true : !InRange || SightBlocked ? false : null;
}

/// <summary>Position-only native geometry queries; normal ground coordinates are kept separate from packet offsets.</summary>
internal static unsafe class CombatPositionNavigation
{
    private const float YOffsetEpsilon = 0.0001f;
    [DllImport("kernel32.dll")] private static extern void AcquireSRWLockShared(nint* address);
    [DllImport("kernel32.dll")] private static extern void ReleaseSRWLockShared(nint* address);
    private static Func<float>? _yOffsetProvider;

    internal static void SetYOffsetProvider(Func<float>? provider) => _yOffsetProvider = provider;

    internal static void ClearYOffsetProvider(Func<float>? provider)
    {
        if (ReferenceEquals(_yOffsetProvider, provider))
            _yOffsetProvider = null;
    }

    internal static float CurrentYOffset
    {
        get
        {
            float value = _yOffsetProvider?.Invoke() ?? 0f;
            return float.IsFinite(value) ? value : 0f;
        }
    }

    internal static bool HasYOffset(float yOffset) => float.IsFinite(yOffset) && MathF.Abs(yOffset) > YOffsetEpsilon;

    internal static Vector3 SightEndpoint(Vector3 position) => position + new Vector3(0, 2, 0);

    public static CombatSight Sight(Vector3 from, Vector3 to, out Vector3 hitPoint) =>
        Sight(from, to, CurrentYOffset, out hitPoint);

    internal static CombatSight Sight(Vector3 from, Vector3 to, float yOffset, out Vector3 hitPoint)
    {
        var sample = SampleSight(from, to, 0f, yOffset);
        if (sample.Normal == CombatSight.Blocked)
        {
            hitPoint = sample.NormalHit;
            return CombatSight.Blocked;
        }
        if (sample.HasOffset && sample.Offset == CombatSight.Blocked)
        {
            hitPoint = sample.OffsetHit;
            return CombatSight.Blocked;
        }
        hitPoint = default;
        return sample.Clear ? CombatSight.Clear : CombatSight.Unknown;
    }

    internal static CombatSight SightBetween(Vector3 origin, Vector3 end, out Vector3 hitPoint, float clearance = 0)
    {
        hitPoint = default;
        var framework = Framework.Instance();
        var module = framework == null ? null : framework->BGCollisionModule;
        if (module == null || module->ShuttingDown || module->SceneManager == null || module->SceneManager->NumScenes == 0 ||
            !CombatPositionPolicy.IsFinite(origin) || !CombatPositionPolicy.IsFinite(end))
            return CombatSight.Unknown;
        var delta = end - origin;
        float length = delta.Length();
        if (!float.IsFinite(length)) return CombatSight.Unknown;
        if (clearance > 0)
        {
            // The swept volume contains the sight line from every start within clearance.
            var sphere = new Vector4(origin, clearance);
            var direction = length > 0 ? delta / length : Vector3.UnitY;
            var filter = new RaycastMaterialFilter { Mask = 0x4000, Value = 0x4000 };
            var args = new RaycastParams
            {
                Algorithm = (int)(OmenTools.Interop.Game.Helpers.RaycastHelper.RaycastMode.SweepSphere |
                                  OmenTools.Interop.Game.Helpers.RaycastHelper.RaycastMode.MaterialFilter),
                Origin = &sphere, Direction = &direction, MaxDistance = &length, MaterialFilter = &filter
            };
            AcquireSRWLockShared(&module->UpdateTaskLock);
            try
            {
                var scenes = module->SceneManager;
                if (module->ShuttingDown || scenes == null || scenes->FirstScene == null || scenes->NumScenes == 0)
                    return CombatSight.Unknown;
                var sweepHit = new RaycastHit();
                foreach (var scene in scenes->Scenes)
                {
                    if (!scene->Raycast(&sweepHit, 1, &args)) continue;
                    hitPoint = sweepHit.Point;
                    return CombatSight.Blocked;
                }
                return CombatSight.Clear;
            }
            finally { ReleaseSRWLockShared(&module->UpdateTaskLock); }
        }
        if (length < 0.001f) return CombatSight.Clear;
        bool blocked = BGCollisionModule.RaycastMaterialFilter(origin, delta / length, out var hit, length);
        if (blocked) hitPoint = hit.Point;
        return blocked ? CombatSight.Blocked : CombatSight.Clear;
    }

    // Check normal height (0) and, when a Y-axis packet offset is active, the actual offset height.
    // Use the 3D distance between centres, without hitbox radii.
    public static CombatAttackCheck CheckAttack(Vector3 point, Vector3 target, float actionRange, float margin = 0,
        float sightClearance = 0) =>
        CheckAttack(point, target, actionRange, CurrentYOffset, margin, sightClearance);

    internal static CombatAttackCheck CheckAttack(Vector3 point, Vector3 target, float actionRange, float yOffset,
        float margin = 0, float sightClearance = 0)
    {
        bool hasOffset = HasYOffset(yOffset);
        bool inRange = CombatPositionPolicy.WithinRange(point, target, actionRange, 0, margin)
            && (!hasOffset || CombatPositionPolicy.WithinRange(point, target, actionRange, yOffset, margin));
        if (!inRange) return new(false, CombatSight.Unknown, CombatSight.Unknown, default, default, hasOffset);
        var sight = SampleSight(point, target, sightClearance, yOffset);
        return new(true, sight.Normal, sight.Offset, sight.NormalHit, sight.OffsetHit, hasOffset);
    }

    internal static CombatSightSample SampleSight(Vector3 player, Vector3 target, float clearance = 0) =>
        SampleSight(player, target, clearance, CurrentYOffset);

    internal static CombatSightSample SampleSight(Vector3 player, Vector3 target, float clearance, float yOffset)
    {
        bool hasOffset = HasYOffset(yOffset);
        var origin = SightEndpoint(player);
        var end = SightEndpoint(target);
        var normal = SightBetween(origin, end, out var normalHit, clearance);
        if (!hasOffset)
            return new(origin, origin, end, normal, normal, normalHit, normalHit, 0f);

        var offsetOrigin = SightEndpoint(player + new Vector3(0, yOffset, 0));
        var offset = SightBetween(offsetOrigin, end, out var offsetHit, clearance);
        return new(origin, offsetOrigin, end, normal, offset, normalHit, offsetHit, yOffset);
    }

    /// <summary>Ground projection via the shared BGCollision raycast:
    /// cast down from seed+UnitY·extent over 2·extent, keep the existing
    /// finite/horizontal/vertical checks, then require the projected
    /// bitmap cell to be free.</summary>
    public static Vector3? BitmapGroundPoint(INavigationGrid map, Vector3 seed,
        float horizontalExtent = 0.4f, float verticalExtent = 1.6f)
    {
        var framework = Framework.Instance();
        var module = framework == null ? null : framework->BGCollisionModule;
        if (module == null || module->ShuttingDown || module->SceneManager == null ||
            module->SceneManager->NumScenes == 0)
            return null;
        var origin = seed + new Vector3(0, verticalExtent, 0);
        if (!BGCollisionModule.RaycastMaterialFilter(origin, new Vector3(0, -1, 0),
                out var hit, verticalExtent * 2f))
            return null;
        var p = hit.Point;
        if (!CombatPositionPolicy.IsFinite(p)) return null;
        float dx = p.X - seed.X, dz = p.Z - seed.Z;
        if (dx * dx + dz * dz > horizontalExtent * horizontalExtent ||
            Math.Abs(p.Y - seed.Y) > verticalExtent)
            return null;
        var (x, y) = map.WorldToGrid(p);
        return map.IsFree(x, y) ? p : null;
    }

}
