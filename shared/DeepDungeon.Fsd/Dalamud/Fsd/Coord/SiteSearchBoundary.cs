using System;
using System.Collections.Generic;
using System.Numerics;
using DeepDungeon.Fsd.Dalamud.GameState;
using DeepDungeon.Fsd.Dalamud.Map;
using DeepDungeon.Fsd.Dalamud.Runtime;
using DeepDungeon.Fsd.Dalamud.Runtime.Floor;
using DeepDungeon.Fsd.Dalamud.Runtime.Search;
using FFXIVClientStructs.FFXIV.Client.Game.InstanceContent;
using MasterPlayer.MechanicRuntime;

namespace DeepDungeon.Fsd.Dalamud.Coord;

/// <summary>Adapts floor observations and typed work outcomes to the route planner.</summary>
internal sealed unsafe class SiteSearchBoundary
{
    private readonly ObjectiveSiteDriver _driver;
    private readonly DdAccessor _dd;
    private readonly Func<Vector3> _playerPos;
    private readonly Func<FloorObjectEvidenceSnapshot?> _evidence;
    private readonly HashSet<int> _quotaSearchedRooms = new();
    private readonly HashSet<int> _quotaFailedRooms = new();

    public QuotaSearchResult NextQuotaSearchRoom(Vector3 playerPosition)
    {
        var dd = _dd();
        if (dd == null) return default;
        var current = RoomGraph.GetLocalPlayerRoomIndex(dd);
        if ((uint)current >= RoomGraph.MaxRooms) return default;
        var rooms = RoomGraph.BuildReachableRoomOrder(dd, current);
        var route = new List<int>();
        QuotaSearchRoom? best = null;
        var bestHops = int.MaxValue;
        var bestDistance = float.PositiveInfinity;
        foreach (var room in rooms)
        {
            if (_quotaSearchedRooms.Contains(room) || _quotaFailedRooms.Contains(room)
                || RoomCenter(room) is not { } center
                || !RoomGraph.TryBuildRoomRoute(dd, current, room, route)) continue;
            var distance = Vector3.DistanceSquared(playerPosition, center);
            if (route.Count > bestHops || route.Count == bestHops && distance >= bestDistance) continue;
            best = new(room, center);
            bestHops = route.Count;
            bestDistance = distance;
        }
        if (best == null)
        {
            // Exhausting one patrol does not complete the kill quota. Repeat
            // the reachable rooms, including rooms whose last approach failed.
            _quotaSearchedRooms.Clear();
            _quotaFailedRooms.Clear();
        }
        return new(true, best);
    }

    public void CompleteQuotaSearchRoom(int roomIndex, bool reached)
    {
        if (reached) _quotaSearchedRooms.Add(roomIndex);
        else _quotaFailedRooms.Add(roomIndex);
    }

    internal SiteSearchBoundary(
        ObjectiveSiteDriver driver, DdAccessor dd, Func<Vector3> playerPos,
        Func<FloorObjectEvidenceSnapshot?>? evidence = null)
    {
        _driver = driver;
        _dd = dd;
        _playerPos = playerPos;
        _evidence = evidence ?? (() => null);
    }

    /// <summary>The controller's once-per-tick activity sample — forwarded
    /// to the driver so passage/combat transitions dirty the route now.</summary>
    internal void SetActivity(in FloorActivity activity) =>
        _driver.SetActivity(activity);

    /// <summary>Per-frame decision tick — driven from the floor update next
    /// to the coverage update, before the coord pipeline ticks.</summary>
    internal void Tick()
    {
        var dd = _dd();
        if (dd != null)
            _driver.TickDecision(dd, _playerPos(), _evidence());
    }

    internal SiteWorkToken HeadToken => _driver.HeadToken;

    internal ChestInteractionAttempt? TakeChestOvercapObservation(string key) =>
        _driver.TakeChestOvercapObservation(key);

    /// <summary>Floor's native hoard acquisition as knowledge sees it —
    /// fed to reveal ops whose postcondition includes acquisition.</summary>
    internal bool KnowledgeHoardAcquired => _driver.KnowledgeHoardAcquired;

    /// <summary>Pipeline→scheduler channel: the bound op reports whether
    /// a meaningful action is in progress; the scheduler preserves the
    /// committed head only while true.</summary>
    internal bool CommitProtected
    {
        set => _driver.CommitProtected = value;
    }

    /// <summary>Typed outcome for a captured work token — updates the
    /// original floor work, never a different head.</summary>
    internal void ReportWorkOutcome(SiteWorkToken token, ObjectiveSite site, SiteWorkOutcome outcome, FailureKind? failure,
        ChestInteractionAttempt? chest, NavigationRegionExhaustion? exhaustion) =>
        _driver.ReportWorkOutcome(token, site, outcome, failure, _evidence(), chest, exhaustion);

    internal static int ReadStoneCount(InstanceContentDeepDungeon* dd)
    {
        if (dd == null) return -1;
        int count = 0;
        foreach (byte stone in dd->Magicite)
            if (stone != 0) count++;
        return count;
    }

    /// <summary>The committed head itself — telemetry reads kind/key/pos.</summary>
    internal ObjectiveSite? Head => _driver.Head;

    private Vector3? RoomCenter(int roomIndex)
    {
        var dd = _dd();
        return dd != null && MapPos.TryGetRoomCenter(dd, roomIndex, out var center) ? center : null;
    }
}
