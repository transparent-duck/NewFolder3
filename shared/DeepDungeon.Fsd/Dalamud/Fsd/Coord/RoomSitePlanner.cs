using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using DeepDungeon.Fsd.Core;
using DeepDungeon.Fsd.Dalamud.GameState;
using DeepDungeon.Fsd.Dalamud.Map;
using DeepDungeon.Fsd.Dalamud.Runtime;
using FFXIVClientStructs.FFXIV.Client.Game.InstanceContent;

namespace DeepDungeon.Fsd.Dalamud.Coord;

/// <summary>
/// Room-level route/cost helpers for the work scheduler: the shared
/// floor-pooled candidate prior (one rank set per floor across all
/// catalog templates), cheap room-graph distances, and a bounded greedy
/// visitation plan. Deliberately not a global optimizer — one shared
/// room route, deterministic insertions.
/// </summary>
internal static class RoomSitePlanner
{
    internal const float MoveSpeedMps = 6f;
    internal const float ProbeSeconds = 3f;

    /// <summary>Cheapest insertion point for a point into an ordered path.</summary>
    internal static (int Index, float ExtraMeters) CheapestInsertion(
        IReadOnlyList<Vector3> path, Vector3 point, Func<Vector3, Vector3, float> distance)
    {
        var best = float.PositiveInfinity;
        var index = 1;
        for (var i = 1; i < path.Count; ++i)
        {
            var extra = MathF.Max(0f, distance(path[i - 1], point) + distance(point, path[i])
                - distance(path[i - 1], path[i]));
            if (extra < best) { best = extra; index = i; }
        }
        return (index, best);
    }

    /// <summary>One-treasure reward per expected second: probability over
    /// travel + probe dwell.</summary>
    internal static double CandidateRate(double probability, float extraMeters) =>
        probability / (extraMeters / 6d + 3d);

    /// <summary>One schedulable candidate point with its calibrated
    /// probability and canonical work key.</summary>
    internal readonly record struct CandidatePoint(
        int RoomIndex,
        string Key,
        RawWorldPosition Position,
        double Probability,
        bool FromModel);

    /// <summary>Result of the per-floor probability build.</summary>
    internal sealed class CandidateModel
    {
        internal bool ModelAvailable;
        /// <summary>False when the floor availability estimate is missing:
        /// membership still applies but point probabilities are zero and
        /// route comparison becomes geometry/service only.</summary>
        internal bool ProbabilityAvailable;
        internal double FloorHoardProbability;
        internal readonly List<CandidatePoint> Points = new();
        /// <summary>Rooms whose distribution could not be calibrated — the
        /// caller falls back to the fixed legacy selection for them.</summary>
        internal readonly HashSet<int> FallbackRooms = new();
        /// <summary>The shared floor-pooled initial set — coordinates whose
        /// pooled rank is selected plus the deterministic unranked fill,
        /// frozen before completion removal and never topped up.</summary>
        internal readonly HashSet<string> InitialProbeKeys = new();
        /// <summary>Unranked coordinates admitted by the deterministic
        /// centroid-order fill — tracked separately, never labeled as a
        /// known rank.</summary>
        internal int UnrankedSlotCount;
        /// <summary>Sight-narrowed direct targets — admissible even outside
        /// the initial set.</summary>
        internal readonly HashSet<string> DirectProbeKeys = new();
    }

    /// <summary>
    /// Policy-owned per-floor pooled profile: ONE shared rank set for the
    /// actual floor aggregated across ALL catalog templates — built once
    /// before completion/Sight filtering, never per room.
    /// </summary>
    internal sealed class FloorProbeProfile
    {
        internal bool Available;
        internal long TotalCount;
        internal long UnknownRankCount;
        internal readonly Dictionary<int, long> RankCounts = new();
        internal readonly HashSet<int> InitialRanks = new();
        internal readonly Dictionary<(int Layout, int Room), int[]> RoomRanks = new();
    }

    /// <summary>
    /// Structural rank mapper (policy layer only — it never changes
    /// published successor facts or declares partial chains complete).
    /// Complete graphs rank by chain order; partial PT21–30 graphs get
    /// one UNIQUE first-floor-positive anchor proposal: a floor-21
    /// positive with an observed predecessor is rank 1, its predecessor
    /// rank 0. Inconsistent/conflicted/cyclic rooms map nothing.
    /// </summary>
    internal static int[] BuildPolicyRanks(
        DetailedMapCatalog catalog, DetailedMapCatalogRoom room)
    {
        var ranks = Enumerable.Repeat(-1, room.Candidates.Length).ToArray();
        var graph = DetailedMapRoomGraphAnalyzer.Analyze(room);
        if (graph.State == DetailedMapRoomGraphPresentationState.Complete)
        {
            for (int rank = 0; rank < graph.CompleteChainOrder.Length; rank++)
            {
                int index = DetailedMapRoomCandidatePlanner.FindUniqueCandidate(
                    room.Candidates, graph.CompleteChainOrder[rank]);
                if (index < 0)
                    return Enumerable.Repeat(-1, room.Candidates.Length).ToArray();
                ranks[index] = rank;
            }
            return ranks;
        }
        if (graph.State == DetailedMapRoomGraphPresentationState.Conflict ||
            catalog.ScenarioKey != "pt-21-30" ||
            !catalog.TryGetFloorIndex(21, out int firstFloorIndex))
            return ranks;

        var incoming = Enumerable.Repeat(-1, room.Candidates.Length).ToArray();
        var outgoing = Enumerable.Repeat(-1, room.Candidates.Length).ToArray();
        foreach (var edge in graph.ObservedEdges)
        {
            int from = DetailedMapRoomCandidatePlanner.FindUniqueCandidate(room.Candidates, edge.Source);
            int to = DetailedMapRoomCandidatePlanner.FindUniqueCandidate(room.Candidates, edge.Target);
            if (from < 0 || to < 0 || incoming[to] >= 0)
                return ranks;
            incoming[to] = from;
            outgoing[from] = to;
        }
        int root = -1;
        for (int i = 0; i < room.Candidates.Length; i++)
        {
            if (room.Candidates[i].HoardCountsByFloor[firstFloorIndex] <= 0 || incoming[i] < 0)
                continue;
            if (root >= 0 && root != incoming[i])
                return ranks;
            root = incoming[i];
        }
        if (root < 0 || incoming[root] >= 0 || outgoing[root] < 0)
            return ranks;
        for (int i = 0; i < room.Candidates.Length; i++)
            if (room.Candidates[i].HoardCountsByFloor[firstFloorIndex] > 0 &&
                i != root && i != outgoing[root])
                return ranks;
        int next = root;
        int depth = 0;
        while (next >= 0)
        {
            if (ranks[next] >= 0)
                return Enumerable.Repeat(-1, room.Candidates.Length).ToArray();
            ranks[next] = depth++;
            next = outgoing[next];
        }
        return ranks;
    }

    /// <summary>Build the shared profile ONCE per pinned catalog/floor —
    /// before completion or Sight is applied. PT21–29 uses the approved
    /// per-floor rank sets independently of their observed coverage.
    /// Other scenarios retain the pooled 95% selection. Unknown-rank
    /// counts stay in the denominator and point priors.</summary>
    internal static FloorProbeProfile BuildFloorProfile(
        DetailedMapCatalog catalog, byte floor)
    {
        var profile = new FloorProbeProfile();
        if (!catalog.TryGetFloorIndex(floor, out int floorIndex))
            return profile;
        foreach (var room in catalog.Rooms)
        {
            var ranks = BuildPolicyRanks(catalog, room);
            profile.RoomRanks[(room.LayoutIndex, room.RoomIndex)] = ranks;
            for (int i = 0; i < room.Candidates.Length; i++)
            {
                long count = room.Candidates[i].HoardCountsByFloor[floorIndex];
                profile.TotalCount += count;
                if (ranks[i] >= 0)
                    profile.RankCounts[ranks[i]] =
                        profile.RankCounts.GetValueOrDefault(ranks[i]) + count;
                else
                    profile.UnknownRankCount += count;
            }
        }
        if (profile.TotalCount <= 0)
            return profile;

        ReadOnlySpan<int> initialRanks = catalog.ScenarioKey == "pt-21-30"
            ? floor switch
            {
                21 => [1],
                22 or 23 => [1, 2],
                24 or 25 or 26 or 27 => [1, 2, 3],
                28 or 29 => [1, 2, 3, 4],
                _ => [],
            }
            : [];
        if (!initialRanks.IsEmpty)
        {
            foreach (int rank in initialRanks)
                profile.InitialRanks.Add(rank);
            profile.Available = true;
            return profile;
        }

        long selected = 0;
        foreach (var pair in profile.RankCounts
            .OrderByDescending(p => p.Value).ThenBy(p => p.Key))
        {
            if (pair.Value <= 0)
                break;
            profile.InitialRanks.Add(pair.Key);
            selected += pair.Value;
            if (selected * 20L >= profile.TotalCount * 19L)
                break;
        }
        profile.Available = selected * 20L >= profile.TotalCount * 19L;
        return profile;
    }

    /// <summary>
    /// Floor-pooled candidate model: every generated legal non-spawn room
    /// contributes its full catalog coordinates; membership is the shared
    /// InitialRanks set plus a deterministic unranked fill; point priors
    /// are the pooled rank counts normalized in-room with equal mass per
    /// legal room. A missing floor estimate yields ProbabilityAvailable=
    /// false (zero probabilities, geometry/service-only route scoring) —
    /// the shared membership STILL applies.
    /// </summary>
    internal static CandidateModel BuildCandidateModel(
        DetailedMapCatalog? catalog,
        HoardYieldCatalog? yield,
        int layoutIndex,
        byte floor,
        int spawnRoom,
        IReadOnlyList<int> rooms,
        IReadOnlyList<RawWorldPosition> observedSight,
        Func<int, RawWorldPosition, string> probeKey,
        Func<string, bool> isProbeCompleted,
        FloorProbeProfile? profile)
    {
        var model = new CandidateModel();
        // A missing or unavailable pinned floor profile keeps the
        // fixed-depth fallback; PT21–29 membership has no coverage gate.
        if (catalog == null || profile == null || !profile.Available)
            return model;

        // The floor availability estimate only scales point priors —
        // without it membership stays usable and probabilities are zero
        // (route comparison becomes geometry/service only).
        HoardYieldFloorEstimate? floorEstimate = null;
        if (yield != null)
            foreach (var e in yield.FloorEstimates)
                if (e.Floor == floor) { floorEstimate = e; break; }
        var pFloor = floorEstimate?.EstimatedHoardProbability ?? 0d;
        model.ProbabilityAvailable =
            floorEstimate != null && double.IsFinite(pFloor) && pFloor > 0d;
        model.FloorHoardProbability = pFloor;

        var universe = new List<(int room, string key, RawWorldPosition pos, double q)>();
        var roomUniverse = new Dictionary<int, List<(string key, RawWorldPosition pos, double w)>>();
        var directByRoom = new Dictionary<int, List<RawWorldPosition>>();
        int legalRooms = Math.Max(1, rooms.Count(r => r != spawnRoom));
        foreach (var room in rooms)
        {
            if (room == spawnRoom)
                continue;
            if (!catalog.TryGetRoom(layoutIndex, room, out var catRoom) ||
                catRoom == null || catRoom.Candidates.Length == 0)
                return model;   // legal room with no catalog room → unavailable
            // The evidence call only answers which targets Sight
            // narrowed — a room's palantir fallback does NOT invalidate
            // the shared floor prior.
            var evidencePlan = DetailedMapRoomCandidatePlanner.BuildPriorityOrder(
                catalog, catRoom, floor, observedSight,
                probeDepthOverride: int.MaxValue);
            if (!profile.RoomRanks.TryGetValue((layoutIndex, room), out var ranks) ||
                ranks.Length != catRoom.Candidates.Length)
                return model;   // unmapped room → shared profile unusable

            var points = new List<(string key, RawWorldPosition pos, double w)>(
                catRoom.Candidates.Length);
            foreach (var cand in catRoom.Candidates)
                points.Add((probeKey(room, cand.Position), cand.Position, 0d));

            // Pooled point weights: shared rank counts normalized over
            // what physically exists in this room — unranked coordinates
            // split the unrepresented mass, a zero-weight room falls to
            // an explicitly uninformed coordinate prior.
            var represented = new HashSet<int>();
            int unknownCoordinates = 0;
            for (int i = 0; i < ranks.Length; i++)
            {
                if (ranks[i] >= 0) represented.Add(ranks[i]);
                else unknownCoordinates++;
            }
            double unrepresented = profile.UnknownRankCount;
            foreach (var pair in profile.RankCounts)
                if (!represented.Contains(pair.Key))
                    unrepresented += pair.Value;
            var weights = new double[points.Count];
            for (int i = 0; i < points.Count; i++)
                weights[i] = ranks[i] >= 0
                    ? profile.RankCounts.GetValueOrDefault(ranks[i])
                    : unknownCoordinates > 0 ? unrepresented / unknownCoordinates : 0d;
            double sum = weights.Sum();
            if (sum > 0d)
                for (int i = 0; i < weights.Length; i++) weights[i] /= sum;
            else
                for (int i = 0; i < weights.Length; i++) weights[i] = 1d / weights.Length;
            for (int i = 0; i < points.Count; i++)
                points[i] = (points[i].key, points[i].pos, weights[i]);

            // Initial membership — shared rank set frozen BEFORE
            // completion removal; missing slots fill with UNRANKED
            // coordinates only (centroid distance, then canonical order)
            // and never borrow a known unselected rank.
            var selected = new HashSet<string>();
            for (int i = 0; i < points.Count; i++)
                if (ranks[i] >= 0 && profile.InitialRanks.Contains(ranks[i]))
                    selected.Add(points[i].key);
            int missing = Math.Max(0, profile.InitialRanks.Count - selected.Count);
            double centerX = points.Average(p => (double)p.pos.X);
            double centerZ = points.Average(p => (double)p.pos.Z);
            foreach (int i in Enumerable.Range(0, points.Count)
                .Where(i => ranks[i] < 0)
                .OrderBy(i =>
                    Math.Pow(points[i].pos.X - centerX, 2) +
                    Math.Pow(points[i].pos.Z - centerZ, 2))
                .ThenBy(i => points[i].pos.X)
                .ThenBy(i => points[i].pos.Y)
                .ThenBy(i => points[i].pos.Z)
                .Take(missing))
            {
                selected.Add(points[i].key);
                model.UnrankedSlotCount++;
            }
            model.InitialProbeKeys.UnionWith(selected);

            if (evidencePlan.DirectCandidates.Count > 0)
            {
                // Every narrowed target must canonical-match a point of the
                // ORIGINAL pre-Sight universe — never fabricate a weight.
                var direct = new List<RawWorldPosition>(evidencePlan.DirectCandidates.Count);
                foreach (var c in evidencePlan.DirectCandidates)
                {
                    var matched = false;
                    foreach (var p in points)
                    {
                        if (!RawWorldPosition.CanonicallyEquals(p.pos, c.Position))
                            continue;
                        matched = true;
                        model.DirectProbeKeys.Add(p.key);
                        break;
                    }
                    if (!matched)
                        return model;
                    direct.Add(c.Position);
                }
                directByRoom[room] = direct;
            }
            roomUniverse[room] = points;
        }
        model.ModelAvailable = true;

        // Pass 1 — equal prior mass per legal non-spawn room; narrowed
        // rooms redistribute that room's mass over direct targets in
        // proportion to pooled weights (uniformly when total is zero).
        var roomMass = 1d / legalRooms;
        foreach (var (room, points) in roomUniverse)
        {
            if (directByRoom.TryGetValue(room, out var direct) && direct.Count > 0)
            {
                var directTotal = 0d;
                foreach (var p in points)
                    if (direct.Any(d => RawWorldPosition.CanonicallyEquals(d, p.pos)))
                        directTotal += p.w;
                foreach (var p in points)
                {
                    if (!direct.Any(d => RawWorldPosition.CanonicallyEquals(d, p.pos)))
                        continue;
                    universe.Add((room, p.key, p.pos,
                        directTotal > 0d ? roomMass * (p.w / directTotal)
                            : roomMass / direct.Count));
                }
                continue;
            }
            foreach (var p in points)
                universe.Add((room, p.key, p.pos, roomMass * p.w));
        }

        // Pass 2 — completed probes remove p·q of the floor's hoard mass;
        // the remainder concentrates without renormalizing so no-hoard
        // mass is preserved. With no floor estimate pFloor is zero and
        // every probability is zero (geometry/service-only scoring).
        var eliminated = 0d;
        foreach (var pt in universe)
            if (isProbeCompleted(pt.key))
                eliminated += pFloor * pt.q;
        foreach (var pt in universe)
            if (!isProbeCompleted(pt.key))
                model.Points.Add(new CandidatePoint(pt.room, pt.key, pt.pos,
                    model.ProbabilityAvailable
                        ? HoardRateMath.RemainingPointProbability(pFloor, pt.q, eliminated)
                        : 0d, true));
        return model;
    }

    /// <summary>Distance in meters between two points through room centers:
    /// same room = XZ leg; different rooms = leg to own center, center-to-
    /// center hops along the flag-graph route, leg into the target. Never
    /// charges hops AND full euclidean for the same segment.</summary>
    internal sealed class RoomDistanceEstimator
    {
        private readonly Dictionary<int, Vector3> _centers = new();
        private readonly Func<int, int, List<int>?> _route;
        private readonly Dictionary<(int, int), List<int>?> _routeCache = new();

        internal RoomDistanceEstimator(
            IReadOnlyList<(int Room, Vector3 Center)> anchors,
            Func<int, int, List<int>?> route)
        {
            foreach (var (r, c) in anchors)
                _centers[r] = c;
            _route = route;
        }

        internal int RoomOf(Vector3 pos)
        {
            var best = -1;
            var bestD = 30f;
            foreach (var (r, c) in _centers)
            {
                var d = Dist2D(pos, c);
                if (d < bestD) { bestD = d; best = r; }
            }
            return best;
        }

        internal List<int>? RouteBetween(int a, int b)
        {
            if (!_routeCache.TryGetValue((a, b), out var route))
                _routeCache[(a, b)] = route = _route(a, b);
            return route;
        }

        internal float Meters(Vector3 a, Vector3 b)
        {
            var ra = RoomOf(a);
            var rb = RoomOf(b);
            if (ra < 0 || rb < 0 || ra == rb)
                return Dist2D(a, b);
            var route = RouteBetween(ra, rb);
            if (route == null || route.Count < 2)
                return Dist2D(a, b);
            var d = Dist2D(a, _centers[route[0]]);
            for (var i = 1; i < route.Count; i++)
                d += Dist2D(_centers[route[i - 1]], _centers[route[i]]);
            d += Dist2D(_centers[route[^1]], b);
            return d;
        }

        internal static float Dist2D(in Vector3 a, in Vector3 b)
        {
            var dx = a.X - b.X;
            var dz = a.Z - b.Z;
            return MathF.Sqrt(dx * dx + dz * dz);
        }
    }

    /// <summary>
    /// Expected search effort of one ordered work list: each site costs
    /// travel + service weighted by the probability the hoard is still
    /// unfound at that point (survival). Authoritative completion
    /// work — exact hoard/indicator and Banded chest — carries p=1 and
    /// resolves the search. A probability map entry overrides the
    /// default for candidate points. NOT a full-run efficiency model:
    /// exit time after a find and combat time are deliberately absent.
    /// </summary>
    internal static double ExpectedSearchSeconds(
        Vector3 start, IReadOnlyList<ObjectiveSite> order,
        Func<Vector3, Vector3, float> distance,
        IReadOnlyDictionary<string, double> probabilities)
    {
        double survival = 1d;
        double seconds = 0d;
        var previous = start;
        foreach (var site in order)
        {
            double service = site.Kind == ObjectiveSiteKind.Candidate
                ? ProbeSeconds : Math.Max(0f, site.TaskCost);
            seconds += Math.Max(0d, survival) *
                (distance(previous, site.Position) / MoveSpeedMps + service);
            double p = probabilities.TryGetValue(site.Key, out var value) ? value :
                site.Kind is ObjectiveSiteKind.Hoard or ObjectiveSiteKind.HoardIndicator ||
                site.Kind == ObjectiveSiteKind.ConfirmedChest &&
                    site.Chest == Runtime.Floor.FloorChestKind.Banded ? 1d : 0d;
            survival = Math.Max(0d, survival - Math.Clamp(p, 0d, 1d));
            previous = site.Position;
        }
        return seconds;
    }

    /// <summary>A selected piece of route work, in visit order.</summary>
    internal readonly record struct RouteWorkItem(
        ObjectiveSite Site,
        float ExtraMeters,
        double Rate);

    /// <summary>
    /// Bounded greedy visitation plan: start+terminal path; mandatory/allowed
    /// chest work inserted by smallest extra meters; optional candidates by
    /// largest CandidateRate strictly above the observed baseline (fallback
    /// mode inserts the selected set with no threshold). Deterministic ties
    /// on canonical key.
    /// </summary>
    internal static List<RouteWorkItem> PlanRoute(
        Vector3 start,
        Vector3 terminal,
        Func<Vector3, Vector3, float> distance,
        IReadOnlyList<ObjectiveSite> mandatory,
        IReadOnlyList<ObjectiveSite> optional,
        IReadOnlyDictionary<string, double> optionalProbability,
        double? observedRate,
        bool rateAvailable)
    {
        var path = new List<Vector3> { start, terminal };
        var order = new List<RouteWorkItem>();
        var pending = new List<ObjectiveSite>(mandatory);
        // Mandatory insertions: cheapest extra meters first.
        while (pending.Count > 0)
        {
            var best = -1;
            var bestExtra = float.PositiveInfinity;
            var bestIndex = 1;
            for (var i = 0; i < pending.Count; i++)
            {
                var (index, extra) = CheapestInsertion(path, pending[i].Position, distance);
                if (extra < bestExtra ||
                    (Math.Abs(extra - bestExtra) < 1e-4f &&
                     pending[i].Key.CompareTo(pending[best].Key) < 0))
                {
                    best = i; bestExtra = extra; bestIndex = index;
                }
            }
            var pick = pending[best];
            path.Insert(bestIndex, pick.Position);
            order.Insert(bestIndex - 1, new RouteWorkItem(pick, bestExtra, 0d));
            pending.RemoveAt(best);
        }
        // Optional candidates: rate-gated marginal yield.
        var remaining = new List<ObjectiveSite>(optional);
        while (remaining.Count > 0)
        {
            var best = -1;
            var bestRate = double.NegativeInfinity;
            var bestExtra = 0f;
            var bestIndex = 1;
            for (var i = 0; i < remaining.Count; i++)
            {
                var (index, extra) = CheapestInsertion(path, remaining[i].Position, distance);
                var p = optionalProbability.TryGetValue(remaining[i].Key, out var v) ? v : 0d;
                var rate = CandidateRate(p, extra);
                if (rate > bestRate ||
                    (Math.Abs(rate - bestRate) < 1e-9 &&
                     remaining[i].Key.CompareTo(remaining[best].Key) < 0))
                {
                    best = i; bestRate = rate; bestExtra = extra; bestIndex = index;
                }
            }
            if (rateAvailable && !(bestRate > (observedRate ?? 0d)))
                break;
            var chosen = remaining[best];
            path.Insert(bestIndex, chosen.Position);
            order.Insert(bestIndex - 1, new RouteWorkItem(chosen, bestExtra, bestRate));
            remaining.RemoveAt(best);
        }
        return order;
    }
}
