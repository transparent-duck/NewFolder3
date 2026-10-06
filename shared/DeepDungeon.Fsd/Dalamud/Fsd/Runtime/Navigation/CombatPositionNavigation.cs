using System.Numerics;
using DeepDungeon.Fsd.Core;
using global::Dalamud.Plugin.Ipc;
using FFXIVClientStructs.FFXIV.Client.System.Framework;
using FFXIVClientStructs.FFXIV.Common.Component.BGCollision;

namespace DeepDungeon.Fsd.Dalamud.Runtime.Navigation;

internal enum CombatSight { Unknown, Clear, Blocked }

/// <summary>Position-only native geometry queries; normal ground coordinates are kept separate from packet offsets.</summary>
internal sealed unsafe class CombatPositionNavigation
{
    private ICallGateSubscriber<Vector3, float, float, Vector3?>? _nearest;
    private ICallGateSubscriber<Vector3, Vector3, bool, CancellationToken, Task<List<Vector3>>>? _path;
    private ICallGateSubscriber<List<Vector3>, bool, object>? _move;
    private ICallGateSubscriber<float, object>? _tolerance;

    public bool Ready
    {
        get
        {
            _path ??= Service.PluginInterface.GetIpcSubscriber<Vector3, Vector3, bool, CancellationToken,
                Task<List<Vector3>>>("vnavmesh.Nav.PathfindCancelable");
            return _path.HasFunction && moveHelper.VNav.NavmeshReady();
        }
    }

    public Vector3? GroundPoint(Vector3 seed, float horizontalExtent = 0.4f, float verticalExtent = 1.6f)
    {
        _nearest ??= Service.PluginInterface.GetIpcSubscriber<Vector3, float, float, Vector3?>("vnavmesh.Query.Mesh.NearestPoint");
        if (!_nearest.HasFunction) return null;
        var point = _nearest.InvokeFunc(seed, horizontalExtent, verticalExtent);
        if (point is not { } p || !CombatPositionPolicy.IsFinite(p)) return null;
        float dx = p.X - seed.X, dz = p.Z - seed.Z;
        return dx * dx + dz * dz <= horizontalExtent * horizontalExtent &&
               Math.Abs(p.Y - seed.Y) <= verticalExtent ? p : null;
    }

    public Task<List<Vector3>> FindPath(Vector3 from, Vector3 to, CancellationToken cancel) =>
        _path!.InvokeFunc(from, to, false, cancel);

    public void Move(List<Vector3> path)
    {
        _move ??= Service.PluginInterface.GetIpcSubscriber<List<Vector3>, bool, object>("vnavmesh.Path.MoveTo");
        _tolerance ??= Service.PluginInterface.GetIpcSubscriber<float, object>("vnavmesh.Path.SetTolerance");
        if (!_move.HasAction) throw new InvalidOperationException("Validated path movement is unavailable.");
        if (_tolerance.HasAction) _tolerance.InvokeAction(CombatPositionPolicy.ArrivalRadius);
        _move.InvokeAction(path, false);
    }

    public static CombatSight Sight(Vector3 from, Vector3 to, out Vector3 hitPoint)
    {
        hitPoint = default;
        var framework = Framework.Instance();
        var module = framework == null ? null : framework->BGCollisionModule;
        if (module == null || module->ShuttingDown || module->SceneManager == null || module->SceneManager->NumScenes == 0 ||
            !CombatPositionPolicy.IsFinite(from) || !CombatPositionPolicy.IsFinite(to))
            return CombatSight.Unknown;
        var origin = from + new Vector3(0, 2, 0);
        var delta = to + new Vector3(0, 2, 0) - origin;
        float length = delta.Length();
        if (length < 0.001f) return CombatSight.Clear;
        bool blocked = BGCollisionModule.RaycastMaterialFilter(origin, delta / length, out var hit, length);
        hitPoint = hit.Point;
        return blocked ? CombatSight.Blocked : CombatSight.Clear;
    }

    public bool HasStandingMargin(Vector3 point, Vector3 target, float actionRange,
        float playerRadius, float targetRadius)
    {
        if (!AttackableAtBothHeights(point, target, actionRange, playerRadius, targetRadius)) return false;
        foreach (var direction in CombatPositionPolicy.Directions)
        {
            var seed = point + new Vector3(direction.X, 0, direction.Y) * CombatPositionPolicy.StandingMargin;
            var grounded = GroundPoint(seed, 0.15f, 0.9f);
            if (grounded is not { } neighbor ||
                !AttackableAtBothHeights(neighbor, target, actionRange, playerRadius, targetRadius)) return false;
        }
        return true;
    }

    private static bool AttackableAtBothHeights(Vector3 point, Vector3 target, float actionRange,
        float playerRadius, float targetRadius) =>
        CombatPositionPolicy.WithinRange(point, target, actionRange, playerRadius, targetRadius, 0, CombatPositionPolicy.RangeMargin) &&
        CombatPositionPolicy.WithinRange(point, target, actionRange, playerRadius, targetRadius, -7, CombatPositionPolicy.RangeMargin) &&
        Sight(point, target, out _) == CombatSight.Clear &&
        Sight(point - new Vector3(0, 7, 0), target, out _) == CombatSight.Clear;
}
