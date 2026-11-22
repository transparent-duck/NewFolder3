using MasterPlayer.MechanicRuntime;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using DeepDungeon.Fsd.Dalamud.GameState;
using DeepDungeon.Fsd.Dalamud.Map;
using DeepDungeon.Fsd.Dalamud.Runtime.Floor;
using DeepDungeon.Fsd.Dalamud.Runtime.Navigation;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using FFXIVClientStructs.FFXIV.Client.Game.InstanceContent;
using FFXIVClientStructs.FFXIV.Client.System.Framework;

namespace DeepDungeon.Fsd.Dalamud.Coord;

/// <summary>Runtime-published navigation diagnostics — populated while
/// the FEC's bitmap navigation exists and cleared with its owner.</summary>
internal static class NavDiag
{
    internal static NavigationSurface? Navigation;
    internal static NavBitmap? Map;
}

/// <summary>
/// Session-free navigation verification. Builds the floor bitmap exactly
/// like the coord pipeline (generated terrain and the current closed doors) then cross-checks flag-graph connectivity against
/// real A* reachability: every open edge must be reachable, every sealed
/// edge must not be. Invoked by /cc fsd nav-debug — no run required.
/// </summary>
internal static class NavDebug
{
    private static NavigationSurface? _gotoNav;
    private static NavBitmap? _gotoMap;
    private static Vector3 _gotoDest;
    private static string _gotoStatus = "";
    private static int _gotoFloor = -1;

    /// <summary>Arm a manual navigation request against a freshly built
    /// floor bitmap — the same construction the coord pipeline uses, so a
    /// goto exercises real wall knowledge. Rebuilds whenever the floor
    /// changes so a stale layout can never drive movement.</summary>
    internal static unsafe string StartGoto(Vector3 dest)
    {
        StopGoto("");
        var dd = EventFramework.Instance()->GetInstanceContentDeepDungeon();
        if (dd == null)
            return "not inside a deep dungeon";
        var pos = Service.LocalPlayer?.Position ?? Vector3.Zero;
        if (!PrepareNavigation(dd)) return FloorMap.Error;
        _gotoDest = new Vector3(dest.X, pos.Y, dest.Z);
        _gotoStatus = "armed";
        return _gotoStatus;
    }

    private static unsafe bool PrepareNavigation(InstanceContentDeepDungeon* dd)
    {
        _gotoMap = null;
        if (FloorMapKey.For(dd) is { } key)
        {
            var template = FloorMap.Get(key);
            if (template == null) return false;
            _gotoMap = NavBitmap.Create(template, dd);
        }
        _gotoNav = new NavigationSurface(() => _gotoMap, null, static () => null);
        _gotoFloor = dd->Floor;
        return true;
    }

    internal static void StopGoto(string status)
    {
        _gotoNav?.Dispose();
        _gotoNav = null;
        _gotoMap = null;
        _gotoStatus = status;
    }

    /// <summary>Per-frame drive for the armed goto — ticked from
    /// FsdEngine.Update so it works with no FSD session.</summary>
    internal static void Tick()
    {
        var nav = _gotoNav;
        if (nav == null)
            return;
        var player = Service.LocalPlayer;
        if (player == null)
        {
            StopGoto("no-player");
            return;
        }
        var st = nav.Navigate(_gotoDest, player.Position, 0.5f);
        _gotoStatus = string.IsNullOrEmpty(nav.Failure)
            ? st.ToString()
            : $"{st} ({nav.Failure})";
        if (st != NavigationState.Moving && !nav.PlanningPending)
            StopGoto(_gotoStatus);
    }

    internal static unsafe object Build()
    {
        var dd = EventFramework.Instance()->GetInstanceContentDeepDungeon();
        if (dd == null)
            return new { ok = false, error = "not inside a deep dungeon" };
        if (FloorMapKey.For(dd) is not { } key)
            return new { ok = true, floor = dd->Floor, terrain = "BMR encounter arena" };
        var template = FloorMap.Get(key);
        if (template == null) return new { ok = false, error = FloorMap.Error };
        var nav = NavBitmap.Create(template, dd);
        var flags = nav.RoomFlags;

        // Material census near the player — the same data the overlay shows,
        // counted so a missing wall family is visible at a glance.
        var census = new Dictionary<string, int>();
        var module = Framework.Instance()->BGCollisionModule;
        var scene = module == null ? null : module->SceneManager == null
            ? null : module->SceneManager->FirstScene->Scene;
        if (scene != null)
        {
            var e = scene->Colliders;
            while (e.MoveNext())
            {
                var c = e.Current;
                if (c == null)
                    continue;
                var materialKey = $"0x{c->ObjectMaterialValue:X}{((c->VisibilityFlags & 1) != 0 ? "+" : "-")}";
                census[materialKey] = census.TryGetValue(materialKey, out var n) ? n + 1 : 1;
            }
        }

        // Edge verification: for each flag-declared pair (rooms i,j sharing
        // a grid edge where both flags name the connection) nav must reach,
        // and for geometric neighbours with no mutual flag it must not.
        var edges = new List<object>();
        var matched = 0; var overblocked = 0; var leaks = 0; var skipped = 0;
        var path = new List<Vector3>();
        for (var i = 0; i < flags.Length; i++)
        {
            if (flags[i] == 0)
                continue;
            foreach (var (j, a, b, dir) in Neighbors(i))
            {
                if (j >= flags.Length || flags[j] == 0)
                    continue;
                var open = (flags[i] & (ushort)a) != 0
                    && (flags[j] & (ushort)b) != 0;
                if (!MapPos.TryGetRoomCenter(dd, i, out var ci)
                    || !MapPos.TryGetRoomCenter(dd, j, out var cj))
                {
                    skipped++;
                    continue;
                }
                var reach = GridAStar.TryFindPath(nav, ci, cj, path);
                if (reach == open) { matched++; continue; }
                // A sealed edge is only a real leak when the flag graph
                // offers no alternate route — a reachable sealed edge with
                // flag connectivity is just the roundabout path.
                if (open) { overblocked++; }
                else if (!RoomGraph.IsRoomReachable(dd, i, j)) { leaks++; }
                else { matched++; continue; }
                edges.Add(new
                {
                    edge = $"{i}{dir}{j}",
                    flagOpen = open,
                    navReachable = reach,
                    verdict = open ? "overblocked" : "leak",
                });
            }
        }

        object? movement = null;
        if (NavDiag.Navigation is { } active && Service.LocalPlayer is { } player)
        {
            var destination = active.FinalDestination;
            var route = new List<Vector3>();
            bool found = GridAStar.TryFindPath(nav, player.Position, destination, route);
            var next = route.FirstOrDefault(p => Vector2.DistanceSquared(
                new(p.X, p.Z), new(player.Position.X, player.Position.Z)) > .01f);
            var origin = player.Position + Vector3.UnitY;
            var end = new Vector3(next.X, player.Position.Y + 1, next.Z);
            var hit = Vector3.Zero;
            var sight = found && next != default
                ? CombatPositionNavigation.SightBetween(origin, end, out hit)
                : CombatSight.Unknown;
            var swept = found && next != default
                ? CombatPositionNavigation.SightBetween(origin, end, out _, player.HitboxRadius)
                : CombatSight.Unknown;
            movement = new
            {
                player = Point(player.Position), destination = Point(destination),
                commanded = Point(active.CommandedTarget), active.InputEnabled,
                active.Status, active.Failure, active.Solver, active.LegIndex,
                found, roomFlags = flags,
                route = route.Take(12).Select(Point).ToArray(),
                pointSight = sight.ToString(),
                pointHit = sight == CombatSight.Blocked ? Point(hit) : null,
                sweptSight = swept.ToString(), clearance = player.HitboxRadius,
            };
        }
        return new
        {
            ok = true,
            territory = Service.ClientState.TerritoryType,
            dungeon = dd->DeepDungeonId,
            floor = dd->Floor,
            tileset = key.Layout,
            activeLayoutIndex = dd->ActiveLayoutIndex,
            layoutInitializationType = dd->LayoutInitializationType,
            playerInTerrain = Service.LocalPlayer is { } local && nav.ContainsWorld(local.Position),
            terrainSource = template.Source,
            sourcePixelSize = template.SourcePixelSize,
            terrainWidth = template.Width,
            terrainHeight = template.Height,
            playerRoom = RoomGraph.GetLocalPlayerRoomIndex(dd),
            rooms = flags.Count(f => f != 0),
            painted = nav.ClosedBoxes,
            matched,
            overblocked,
            leaks,
            skipped,
            anomalies = edges,
            movement,
            census,
            navgoto = _gotoStatus,
        };

        static object Point(Vector3 p) => new { x = p.X, y = p.Y, z = p.Z };
    }

    // Grid neighbours (5x5) with the two opposing direction flags —
    // S(+5)/E(+1) only, so each unordered pair is emitted once.
    private static IEnumerable<(int j,
        InstanceContentDeepDungeon.RoomFlags a,
        InstanceContentDeepDungeon.RoomFlags b, char dir)> Neighbors(int i)
    {
        if (i / 5 < 4) yield return (i + 5,
            InstanceContentDeepDungeon.RoomFlags.ConnectionS,
            InstanceContentDeepDungeon.RoomFlags.ConnectionN, 'S');
        if (i % 5 < 4) yield return (i + 1,
            InstanceContentDeepDungeon.RoomFlags.ConnectionE,
            InstanceContentDeepDungeon.RoomFlags.ConnectionW, 'E');
    }
}
