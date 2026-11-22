using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using DeepDungeon.Fsd.Core;
using DeepDungeon.Fsd.Dalamud.GameState;
using DeepDungeon.Fsd.Dalamud.Map;
using DeepDungeon.Fsd.Dalamud.Runtime;
using DeepDungeon.Fsd.Dalamud.Runtime.Floor;
using DeepDungeon.Fsd.Dalamud.Runtime.Navigation;
using FFXIVClientStructs.FFXIV.Client.Game.InstanceContent;
using MasterPlayer.MechanicRuntime;

namespace DeepDungeon.Fsd.Dalamud.Coord;

/// <summary>
/// The site-pool decision driver — owns the site ledger, the intent
/// (committed head + advisory tail) and, in knowledge-driven mode, the
/// shared room-route selection. Navigation is deliberately not owned: the
/// caller reads Head, drives movement however the layer requires
/// (NavigationSurface, GoToVerb claims, ops), then reports the typed
/// outcome back through the captured work token.
///
/// Knowledge mode is the normal-floor path: candidates come from the
/// calibrated yield model (legacy fixed selection as the per-room
/// fallback), work completes through probe/chest/intel postconditions in
/// FloorKnowledge, and route order — not reward argmax — decides the head.
/// </summary>
internal sealed class ObjectiveSiteDriver : IDisposable
{
    private readonly List<ObjectiveSite> _sites = new();
    private string _committedKey = "";
    private SiteWorkToken _committedToken;
    private long _lastRebuildMs;
    private ObjectiveSiteSource.ObjectiveSiteFilter _filter =
        new(OpenBronze: true, OpenSilver: true, OpenGold: true,
            Banded: true, HuntMobs: true, ProbeRooms: true, Exit: true);

    // ---- knowledge-driven mode ----
    private DetailedMapRunSnapshot? _map;
    private RunKnowledge? _knowledge;
    private Func<FloorWorkPolicy>? _policy;
    private Action<string, object>? _record;
    private FloorWorkPolicy _workPolicy;
    private bool _routeDirty = true;
    private long _seenKnowledgeRevision = -1;
    private long _seenInventoryRevision = -1;
    private long _seenEvidenceVersion = -1;
    private long _nextAttempt;
    private readonly Dictionary<string, long> _attempts = new();
    private readonly List<RoomSitePlanner.RouteWorkItem> _routeWork = new();
    private readonly List<(int Room, Vector3 Pos)> _anchors = new();
    private readonly List<int> _rooms = new();
    private readonly PalantirProvider _palantir = new();
    private NormalFloorGraphSnapshot? _normalGraph;
    private FloorActivity _activity;
    // Floor-level geometry: anchors, legal rooms, pairwise room paths and
    // the distance estimator are built ONCE per floor — never per rebuild.
    private long _geometryGeneration = -1;
    private RoomSitePlanner.RoomDistanceEstimator? _dist;
    private readonly Dictionary<(int From, int To), List<int>?> _roomPaths = new();
    private bool _routePassageOpen;
    private bool _routeRebuilt;
    private string _routeState = "";
    private string _routeSource = "";
    private double? _routeRate;
    private string _routeSkipped = "";
    private int _routeProductive;
    private bool _routeResolved;
    private RoomSitePlanner.CandidateModel? _model;
    private FloorKnowledge? _modelFloor;
    private int _modelSightCount = -1;
    private int _modelProbeCount = -1;
    // Shared floor-pooled profile — built once per pinned catalog+floor;
    // the structural rank mapping is cached here, never per frame.
    private RoomSitePlanner.FloorProbeProfile? _floorProfile;
    private DetailedMapCatalog? _profileCatalog;
    private int _profileFloor = -1;
    /// <summary>Pipeline→scheduler channel: true while the bound op has a
    /// meaningful action in progress (dwell/interaction), false during
    /// plain travel.</summary>
    internal bool CommitProtected { get; set; }
    internal bool KnowledgeHoardAcquired => _knowledge?.Floor?.HoardAcquired == true;
    internal string? BlockedWorkReason { get; private set; }
    internal bool PassageSearchExhausted { get; private set; }

    internal void SetNavigationMap(NavBitmap? map)
    {
        if (ReferenceEquals(_navigationMap, map)) return;
        _navigationMap = map;
        _observedPassageWork.Clear();
        foreach (var key in _failedWorkKeys.Where(pair => pair.Value.Failure == FailureKind.Unreachable)
                     .Select(pair => pair.Key).ToArray())
            _failedWorkKeys.Remove(key);
        _routeDirty = true;
    }

    /// <summary>The most recently ticking driver — read by the world
    /// overlay. Self-registered in TickDecision, cleared on Dispose.</summary>
    internal static ObjectiveSiteDriver? Active { get; private set; }


    internal ObjectiveSite? Head { get; private set; }
    internal SiteWorkToken HeadToken => _committedToken;

    /// <summary>Chase-position sampler for the quota site — injected by the
    /// host; frozen at each rebuild.</summary>
    internal Func<Vector3?>? ChasePositionProvider;
    internal IReadOnlyList<ObjectiveSite> Sites => _sites;
    internal IReadOnlyList<RoomSitePlanner.RouteWorkItem> RouteWork => _routeWork;
    internal string Status { get; private set; } = "idle";

    internal int OpenCount
    {
        get
        {
            var n = 0;
            foreach (var s in _sites)
                if (s.Status == ObjectiveSiteStatus.Open)
                    n++;
            return n;
        }
    }

    /// <summary>Drop the committed head without an outcome — the boundary's
    /// context-clear semantic.</summary>
    internal void ClearCommit()
    {
        _committedKey = "";
        _committedToken = default;
        Head = null;
    }

    internal void Configure(
        ObjectiveSiteSource.ObjectiveSiteFilter filter)
    {
        _filter = filter;
    }

    /// <summary>Bind the knowledge-driven normal path: the run-pinned map
    /// snapshot (never DetailedMapCatalogManager.Active), the shared run
    /// knowledge, the mode policy, the authoritative passage-open state
    /// (duty flag, not object-range actors), and decision telemetry.</summary>
    private Func<ChestInteractionAttempt, bool>? _canClearChestOvercap;

    internal void ConfigureWork(
        DetailedMapRunSnapshot map,
        RunKnowledge knowledge,
        Func<FloorWorkPolicy> policy,
        Action<string, object> record,
        Func<ChestInteractionAttempt, bool> canClearChestOvercap)
    {
        _map = map;
        _knowledge = knowledge;
        _policy = policy;
        _record = record;
        _canClearChestOvercap = canClearChestOvercap;
        BlockedWorkReason = null;
        _routeDirty = true;
    }

    /// <summary>Force the next decision tick to rebuild the route —
    /// RequestPlanRefresh's only effect in the normal path.</summary>
    internal void MarkRouteDirty() => _routeDirty = true;

    internal void SetNormalGraph(NormalFloorGraphSnapshot? graph)
    {
        if (ReferenceEquals(_normalGraph, graph))
            return;
        _normalGraph = graph;
        _geometryGeneration = -1;   // legal rooms re-derive on next build
        _modelFloor = null;         // and the model shares that universe
    }

    /// <summary>The floor's live activity pair, sampled once by the
    /// controller after ingest — a passage/combat transition invalidates
    /// the route immediately instead of waiting for the next cadence.</summary>
    internal void SetActivity(in FloorActivity activity)
    {
        if (_activity == activity)
            return;
        _activity = activity;
        _routeDirty = true;
    }

    /// <summary>Floor-level geometry cache: anchors + legal rooms + all
    /// pairwise room paths + the distance estimator, built once per floor
    /// generation. Saved paths live in a dictionary — no dd pointer is
    /// captured by the cached estimator.</summary>
    private unsafe void EnsureGeometry(InstanceContentDeepDungeon* dd, FloorKnowledge fk)
    {
        if (_geometryGeneration == fk.Generation && _dist != null)
            return;
        _geometryGeneration = fk.Generation;
        _anchors.Clear();
        _rooms.Clear();
        // Geometry universe = this floor's actual room graph — the layout
        // centers table spans more rooms than the generated floor has.
        foreach (var r in _normalGraph!.ReachableRooms)
            if (MapPos.TryGetRoomCenter(dd, r, out var c))
            {
                _anchors.Add((r, c));
                _rooms.Add(r);
            }
        _roomPaths.Clear();
        foreach (var a in _rooms)
            foreach (var b in _rooms)
            {
                if (a == b)
                    continue;
                var route = new List<int>();
                _roomPaths[(a, b)] = RoomGraph.TryBuildRoomRoute(dd, a, b, route)
                    ? route : null;
            }
        _dist = new RoomSitePlanner.RoomDistanceEstimator(_anchors,
            (a, b) => _roomPaths.TryGetValue((a, b), out var p) ? p : null);
    }

    internal static string SiteKey(in ObjectiveSite s) =>
        s.Key.Length > 0
            ? s.Key
            : $"{(int)s.Kind}:{(int)(s.Position.X * 2f)}:{(int)(s.Position.Z * 2f)}";

    /// <summary>Per-frame decision tick. Evidence is injected — the driver
    /// never owns an observation scan.</summary>
    internal unsafe void TickDecision(
        InstanceContentDeepDungeon* dd,
        Vector3 playerPos,
        FloorObjectEvidenceSnapshot? evidence)
    {
        Active = this;
        if (dd == null || evidence?.PlayerPosition == null)
        {
            Status = evidence == null ? "no-evidence" : "no-dd";
            Head = null;
            return;
        }
        TickKnowledgeDecision(dd, playerPos, evidence);
    }

    private unsafe void TickKnowledgeDecision(
        InstanceContentDeepDungeon* dd,
        Vector3 playerPos,
        FloorObjectEvidenceSnapshot evidence)
    {
        var fk = _knowledge!.Floor;
        if (fk == null)
        {
            Status = "no-floor-knowledge";
            Head = null;
            return;
        }

        var nowMs = Environment.TickCount64;
        int stoneCount = _chestCapacity.Count == 0 ? -1 : SiteSearchBoundary.ReadStoneCount(dd);
        foreach (var (key, capacity) in _chestCapacity)
        {
            var (attempt, wasBlocked) = capacity;
            bool blocked = !(attempt.PendingGoldOvercapSlotIndex is { } slot && _knowledge.GetPomanderStock(slot) < 3) &&
                !(attempt.PendingSilverOvercapStoneRowId.HasValue && stoneCount is >= 0 and < 3) &&
                _canClearChestOvercap?.Invoke(attempt) != true;
            if (wasBlocked == blocked) continue;
            _chestCapacity[key] = (attempt, blocked);
            _routeDirty = true;
        }
        var playerRoom = RoomGraph.GetLocalPlayerRoomIndex(dd);
        var dirty = _routeDirty
            || fk.Revision != _seenKnowledgeRevision
            || _knowledge.InventoryRevision != _seenInventoryRevision
            || evidence.Version != _seenEvidenceVersion
            || nowMs - _lastRebuildMs > 1500;
        if (playerRoom >= 0 && playerRoom != _failureOriginRoom)
        {
            _failureOriginRoom = playerRoom;
            foreach (var key in _failedWorkKeys.Where(item =>
                         item.Value.Failure == FailureKind.Unreachable &&
                         item.Value.Kind is ObjectiveSiteKind.Candidate or ObjectiveSiteKind.RoomProbe or ObjectiveSiteKind.PassageSearch)
                         .Select(item => item.Key).ToArray())
                dirty |= _failedWorkKeys.Remove(key);
        }
        if (dirty)
        {
            _lastRebuildMs = nowMs;
            RebuildWorkRoute(dd, evidence, fk, playerPos, playerRoom);
        }
        if (_routeWork.Count == 0)
        {
            // Non-route work may still exist (quota/passage sites are
            // route-independent commitments).
            ObjectiveSite? lone = null;
            foreach (var s in _sites)
                if (s.Status == ObjectiveSiteStatus.Open &&
                    s.Kind is ObjectiveSiteKind.Quota or ObjectiveSiteKind.Passage or ObjectiveSiteKind.PassageSearch)
                { lone = s; break; }
            CommitHead(lone);
            Status = lone.HasValue ? $"head={lone.Value.Kind} route=empty" : BlockedWorkReason ?? "work-resolved";
            EmitRouteRecord(fk);
            return;
        }

        // Head = the first item on the selected route. The committed head
        // survives only while the pipeline reports a meaningful action in
        // progress (probe dwell awaiting postcondition, dispatched chest
        // interaction) — plain travel does not protect it, so work
        // discovered on route can become head without thrashing mid-
        // interaction. Work no longer needed is removed regardless.
        // Quota preempts only to finish combat before an open passage —
        // incidental combat alongside useful room work does not force it.
        ObjectiveSite? head = _routeWork[0].Site;
        // No passage-open quota preemption here: quota exists only when no
        // useful work remains, so the route's useful work keeps its head
        // while combat runs as a side channel; quota reaches head only
        // when the route is empty (the lone-head path above).
        if (CommitProtected && _committedKey.Length > 0)
        {
            for (var i = 0; i < _routeWork.Count; i++)
                if (SiteKey(_routeWork[i].Site) == _committedKey &&
                    _routeWork[i].Site.Status == ObjectiveSiteStatus.Open)
                {
                    head = _routeWork[i].Site;
                    break;
                }
        }
        CommitHead(head);
        EmitRouteRecord(fk);

        Status = head.HasValue
            ? $"head={head.Value.Kind}@{head.Value.RoomIndex} route={_routeWork.Count} " +
              $"rate={(fk.ActiveHoardRate?.ToString("0.000") ?? fk.SelectionSource)}"
            : "route-empty";
    }

    /// <summary>site-route telemetry — emitted only after CommitHead so
    /// the record carries the real committed head and its work token,
    /// the actually selected/skipped keys, and the mode inputs.</summary>
    private void EmitRouteRecord(FloorKnowledge fk)
    {
        if (!_routeRebuilt)
            return;
        _routeRebuilt = false;
        var p = _workPolicy;
        _record?.Invoke("site-route", new
        {
            floor = fk.Floor,
            state = _routeState,
            source = _routeSource,
            rate = _routeRate,
            mode = $"hoard={p.SearchHoard} discovery={p.DiscoveryOnly} " +
                   $"harvest={p.HarvestChests}",
            token = !string.IsNullOrEmpty(_committedToken.Key)
                ? $"{_committedToken.Key}#{_committedToken.Attempt}"
                : null,
            head = Head is { } h ? $"{h.Kind}:{SiteKey(h)}" : null,
            route = _routeWork.Count,
            selected = SelectedKeys(),
            skipped = _routeSkipped,
            productive = _routeProductive,
            passageOpen = _routePassageOpen,
            resolved = _routeResolved,
            // pooled-profile telemetry: membership availability, the
            // deterministic unranked fill count and whether point
            // probabilities carry a real floor estimate.
            profileAvailable = _floorProfile?.Available == true,
            unrankedSlots = _model?.UnrankedSlotCount ?? 0,
            probabilityAvailable = _model?.ProbabilityAvailable == true,
        });
    }

    private void CommitHead(ObjectiveSite? head)
    {
        Head = head;
        if (head.HasValue)
        {
            var key = SiteKey(head.Value);
            if (key != _committedKey)
            {
                _committedKey = key;
                // Rebind the already-registered attempt for this floor+key
                // — a suspend/resume or route re-selection is NOT a fresh
                // execution; a new attempt exists only for first commit.
                var generation = _knowledge!.Floor?.Generation ?? 0;
                if (!_attempts.TryGetValue(key, out var attempt))
                    _attempts[key] = attempt = ++_nextAttempt;
                _committedToken = new SiteWorkToken(generation, key, attempt);
            }
        }
        else
        {
            _committedKey = "";
            _committedToken = default;
        }
    }

    private unsafe void RebuildWorkRoute(
        InstanceContentDeepDungeon* dd,
        FloorObjectEvidenceSnapshot evidence,
        FloorKnowledge fk,
        Vector3 playerPos,
        int playerRoom)
    {
        _routeDirty = false;
        _seenKnowledgeRevision = fk.Revision;
        _seenInventoryRevision = _knowledge!.InventoryRevision;
        _seenEvidenceVersion = evidence.Version;
        var policy = _policy?.Invoke() ?? default;
        _workPolicy = policy;

        // Floor geometry (anchors, legal rooms, pairwise paths, distance
        // estimator) is built once per floor generation, not per rebuild.
        EnsureGeometry(dd, fk);
        var dist = _dist!;
        List<int>? roomRoute(int a, int b) => dist.RouteBetween(a, b);

        // Exact reveal position through the existing resolver — not
        // GeneratePlan: only the state is needed here.
        var exact = fk.ExactHoardPosition;
        var exactRoom = fk.ExactHoardRoomIndex;
        if (exact.HasValue && exactRoom == null && dist.RoomOf(exact.Value) is var er0 && er0 >= 0)
            exactRoom = er0;

        // Cached calibrated candidate model — rebuilt only when the Sight
        // or completed-probe inputs change (or the floor's knowledge
        // object did), never just because the player/evidence moved.
        var rate = _knowledge!.ObservedHoardRate();
        fk.ActiveHoardRate = rate;
        var observedSight = fk.ObservedSightPositions;
        var spawnRoom = fk.SpawnRoom >= 0 ? fk.SpawnRoom : playerRoom;
        // Shared floor-pooled profile — built ONCE per pinned
        // catalog+floor before completion/Sight filtering, cached on the
        // driver so the structural mapping runs once, not per frame.
        if (_profileFloor != dd->Floor ||
            !ReferenceEquals(_profileCatalog, _map?.Catalog))
        {
            _floorProfile = _map?.Catalog != null
                ? RoomSitePlanner.BuildFloorProfile(_map.Catalog, dd->Floor)
                : null;
            _profileFloor = dd->Floor;
            _profileCatalog = _map?.Catalog;
        }
        if (_modelFloor != fk || _modelSightCount != observedSight.Count ||
            _modelProbeCount != fk.CompletedProbeCount)
        {
            _model = RoomSitePlanner.BuildCandidateModel(
                _map?.Catalog, _map?.HoardYield, dd->ActiveLayoutIndex,
                dd->Floor, spawnRoom, _rooms, observedSight,
                (room, pos) => fk.GetProbeKey(room, pos), fk.IsProbeCompleted,
                _floorProfile);
            _modelFloor = fk;
            _modelSightCount = observedSight.Count;
            _modelProbeCount = fk.CompletedProbeCount;
        }
        var model = _model!;
        // Model availability alone gates model selection — the observed
        // current-run rate is telemetry, never an admission threshold.
        var useModelSelection = model.ModelAvailable;

        DeepDungeonFloorsetTracker.TryGetCurrentFloorsetState(
            dd->Floor, out var floorsetState);
        var opportunity = FloorsetHoardDistributionPolicy.Decide(floorsetState, dd->Floor);
        var state = HoardEvidencePolicy.Resolve(new HoardEvidenceSnapshot
        {
            BandedEnabled = policy.SearchHoard,
            HoardOpenedThisFloor = fk.HoardAcquired,
            ChatSaysNoHoard = fk.ChatSaysNoHoard,
            ExactHoardObserved = exact.HasValue || fk.ObservedBandedPositions.Count > 0,
            InheritedNoHoardInferred = fk.InheritedNoHoardInferred,
            CachedHoardIndicatorRoomIndex = exactRoom,
            ChatSaysHoard = fk.ChatSaysHoard,
            UsedIntuitionThisFloor = fk.UsedIntuitionThisFloor,
            AwaitingIntuitionResult = fk.IntuitionUsePendingThisFloor,
            IntuitionActive = fk.NativeIntuitionActive,
            FloorsetHoardOpportunity = opportunity,
        });
        // FloorsetDistributionExcluded is terminal no-search handling —
        // never masqueraded back as BlindSearch for cost selection.
        fk.EvidenceState = state;

        // ---- candidate pool: calibrated model OR the fixed fallback ----
        var candidates = new List<(int Room, string Key, Vector3 Pos, float P)>();
        var optionalProb = new Dictionary<string, double>();
        var candidatesOn = policy.SearchHoard && !fk.HoardAcquired &&
            !exact.HasValue && state == HoardEvidenceState.BlindSearch;
        if (candidatesOn)
        {
            if (useModelSelection)
            {
                // The shared floor-pooled initial set ∪ genuine Sight
                // directs is the admission; every model point still
                // contributes its pooled probability to route scoring.
                foreach (var pt in model.Points)
                {
                    optionalProb[pt.Key] = pt.Probability;
                    if (model.InitialProbeKeys.Contains(pt.Key) ||
                        model.DirectProbeKeys.Contains(pt.Key))
                        candidates.Add((pt.RoomIndex, pt.Key,
                            new Vector3(pt.Position.X, pt.Position.Y, pt.Position.Z),
                            (float)pt.Probability));
                }
                fk.SelectionSource = "floor-coverage-95";
            }
            else
            {
                // Fixed selection = the existing per-room K/Direct set with
                // Palantir fallback — never the full calibrated universe.
                fk.SelectionSource = "fallback";
                foreach (var room in _rooms)
                {
                    if (room == spawnRoom)
                        continue;
                    var added = false;
                    if (_map?.Catalog != null &&
                        _map.Catalog.TryGetRoom(dd->ActiveLayoutIndex, room, out var catRoom))
                    {
                        var plan = DetailedMapRoomCandidatePlanner.BuildPriorityOrder(
                            _map.Catalog, catRoom, dd->Floor, observedSight);
                        if (!plan.UsePalantirFallback)
                        {
                            added = true;
                            foreach (var c in plan.DirectCandidates)
                            {
                                var key = fk.GetProbeKey(room, c.Position);
                                candidates.Add((room, key,
                                    new Vector3(c.Position.X, c.Position.Y, c.Position.Z), 0f));
                            }
                            foreach (var c in plan.OrdinaryCandidates)
                            {
                                var key = fk.GetProbeKey(room, c.Position);
                                candidates.Add((room, key,
                                    new Vector3(c.Position.X, c.Position.Y, c.Position.Z), 0f));
                            }
                        }
                    }
                    if (!added)
                        foreach (var p in _palantir.GetCandidatePositionsForRoom(dd, room))
                            candidates.Add((room,
                                fk.GetProbeKey(room, new RawWorldPosition(p.X, p.Y, p.Z)),
                                p, 0f));
                }
            }
        }

        // ---- room intel work (purpose-scoped, never every room) ----
        // Only HOARD candidates and the Intuition-position search are
        // excluded from the spawn room (spawn-no-hoard rule);
        // harvest intel legitimately includes it.
        var intel = new List<(int Room, Vector3 Pos, string Purpose)>();
        void AddIntel(RoomIntelPurpose purpose, bool excludeSpawn)
        {
            var name = purpose.ToString();
            foreach (var (room, pos) in _anchors)
            {
                if ((excludeSpawn && room == spawnRoom) ||
                    fk.IsIntelCompleted(room, purpose))
                    continue;
                intel.Add((room, pos, name));
            }
        }
        if (!fk.HoardAcquired &&
            state is HoardEvidenceState.IntuitionWaitingForIndicator
                or HoardEvidenceState.IntuitionActiveUnconfirmed)
            AddIntel(RoomIntelPurpose.IntuitionPosition, true);
        if (policy.HarvestChests)
            AddIntel(RoomIntelPurpose.Discovery, false);

        // ---- produce the legal possibility pool ----
        var noHoardTerminal = fk.HoardAcquired ||
            state is HoardEvidenceState.IntuitionNoHoard
                or HoardEvidenceState.Disabled or HoardEvidenceState.AlreadyOpened
                or HoardEvidenceState.FloorsetMaxed;
        var filter = new ObjectiveSiteSource.ObjectiveSiteFilter(
            OpenBronze: policy.OpenBronze,
            OpenSilver: policy.OpenSilver,
            OpenGold: policy.OpenGold,
            // Acquired/NoHoard also filters LIVE lingering indicator sites,
            // not only the physical banded chest work.
            Banded: !policy.DiscoveryOnly &&
                (policy.SearchHoard || policy.HarvestChests) && !noHoardTerminal,
            HuntMobs: true, ProbeRooms: true, Exit: true);
        var produced = ObjectiveSiteSource.Produce(
            evidence, filter, playerRoom, spawnRoom, _anchors, candidates, intel);
        // Raw live indicator sites are a debug/legacy affordance: on the
        // normal path the single cached exact Hoard work from knowledge is
        // the one job at that point — no ind:* + hoard:* duplicates.
        produced.RemoveAll(s => s.Kind == ObjectiveSiteKind.HoardIndicator);

        // Knowledge-observed chests survive object unload: emit work for
        // them even when the entity is not in the live snapshot — the
        // cached position navigates, live evidence confirms the open.
        if (!policy.DiscoveryOnly)
            foreach (var c in fk.ObservedChests)
            {
                if (fk.IsChestCompleted(c.EntityId))
                    continue;
                var kind = (FloorChestKind)c.Kind;
                var allowed = kind switch
                {
                    FloorChestKind.Gold => policy.OpenGold,
                    FloorChestKind.Silver => policy.OpenSilver,
                    FloorChestKind.Banded => filter.Banded,
                    _ => policy.OpenBronze,
                };
                if (!allowed)
                    continue;
                var live = evidence.Available &&
                    evidence.Chests.Any(ch => ch.Object.EntityId == c.EntityId);
                if (live)
                    continue;   // the live evidence already produced it
                produced.Add(new ObjectiveSite(
                    -3, ObjectiveSiteKind.ConfirmedChest, c.RoomIndex, c.Position,
                    ObjectiveSiteSource.RewardOf(ObjectiveSiteKind.ConfirmedChest, kind),
                    3f, ObjectiveSiteStatus.Open,
                    Chest: kind, Key: $"chest:{c.EntityId}"));
            }

        // A capacity block leaves a chest out while that inventory slot
        // still has no space — filtered AFTER live AND remembered chest
        // generation, BEFORE admission/required counts; never recorded as
        // completed.
        if (_chestCapacity.Count > 0)
            produced.RemoveAll(s =>
                s.Kind == ObjectiveSiteKind.ConfirmedChest &&
                s.Key is { } ck && _chestCapacity.TryGetValue(ck, out var capacity) && capacity.Blocked);

        // Exact-known hoard: a destination requirement that bypasses
        // optional candidate selection entirely — until its coffer exists,
        // when the physical Banded chest work replaces it.
        bool BandedNear(Vector3 pos) =>
            fk.ObservedChests.Any(c => c.Kind == (int)FloorChestKind.Banded &&
                Vector3.Distance(c.Position, pos) < 4f) ||
            (evidence.Available &&
             Runtime.Search.BandedChestLocator.TryFindNearestAround(
                 evidence, pos, 4f, out var found) && found.HasValue);
        if (exact.HasValue && !fk.HoardAcquired && policy.SearchHoard &&
            !BandedNear(exact.Value))
            produced.Add(new ObjectiveSite(
                -2, ObjectiveSiteKind.Hoard, exactRoom ?? -1, exact.Value,
                1f, 3f, ObjectiveSiteStatus.Open,
                Key: $"hoard:{exact.Value.X:0.###}:{exact.Value.Y:0.###}:{exact.Value.Z:0.###}"));

        // Work failures stay scoped to the observation that produced them —
        // excluded AFTER live/remembered/exact generation, BEFORE the
        // completion merge and admission/required counts; never recorded
        // as completed.
        bool blindCoverageGap = state == HoardEvidenceState.BlindSearch &&
            candidates.Any(candidate => _failedWorkKeys.ContainsKey(candidate.Key) &&
                !fk.IsProbeCompleted(candidate.Key));
        ExcludeFailedWork(produced, playerPos);

        // Status merge: completion truth lives in knowledge, not in the
        // previous pool, so an omitted-then-reproduced site cannot revive
        // (no permanent Retryable carryover on the normal path).
        for (var i = 0; i < produced.Count; i++)
        {
            var s = produced[i];
            var key = SiteKey(s);
            if (s.Kind == ObjectiveSiteKind.Candidate && fk.IsProbeCompleted(key))
                produced[i] = s with { Status = ObjectiveSiteStatus.Done, Reason = "probed" };
            else if (s.Kind == ObjectiveSiteKind.RoomProbe &&
                     TryIntelPurpose(key, out var pr, out var room) &&
                     fk.IsIntelCompleted(room, pr))
                produced[i] = s with { Status = ObjectiveSiteStatus.Done, Reason = "intel" };
            else if (s.Kind == ObjectiveSiteKind.ConfirmedChest &&
                     TryChestEntity(key, out var entity) && fk.IsChestCompleted(entity))
                produced[i] = s with { Status = ObjectiveSiteStatus.Done, Reason = "opened" };
            else if (s.Kind is ObjectiveSiteKind.Hoard or ObjectiveSiteKind.HoardIndicator &&
                     noHoardTerminal)
                produced[i] = s with { Status = ObjectiveSiteStatus.Done, Reason = "hoard-resolved" };
            else if (s.Kind == ObjectiveSiteKind.HoardIndicator && BandedNear(s.Position))
                // The point's coffer exists — the physical Banded chest
                // work replaces indicator/probe work at the same point.
                produced[i] = s with { Status = ObjectiveSiteStatus.Done, Reason = "revealed" };
        }

        // Authoritative activity — the captured normal-decision sample,
        // not an independent Service.Condition read.
        var passageOpen = _activity.PassageOpen;
        _routePassageOpen = passageOpen;
        var inCombatNow = _activity.InCombat;
        var passage = SelectPassageGoal(dd, evidence, playerPos, dist, out bool searchExhausted);
        _selectedPassage = passage;
        var passageRoom = passage?.Room ?? -1;
        var passagePos = passage?.Position ?? playerPos;

        // ---- required goal work vs optional ordinary chests ----
        // Required: exact reveal work, purpose intel, and the chests the
        // MODE makes required — Banded and harvest-mode chests.
        // Ordinary chests are never a
        // destination — admitted only inside rooms already on the route.
        var requiredSites = new List<ObjectiveSite>();
        var chestSites = new List<ObjectiveSite>();
        var candidateSites = new List<ObjectiveSite>();
        foreach (var s in produced)
        {
            if (s.Status != ObjectiveSiteStatus.Open)
                continue;
            switch (s.Kind)
            {
                case ObjectiveSiteKind.Hoard:
                case ObjectiveSiteKind.HoardIndicator:
                case ObjectiveSiteKind.RoomProbe:
                    requiredSites.Add(s);
                    break;
                case ObjectiveSiteKind.ConfirmedChest:
                    if (s.Chest == FloorChestKind.Banded || policy.HarvestChests)
                        requiredSites.Add(s);
                    else
                        chestSites.Add(s);
                    break;
                case ObjectiveSiteKind.Candidate:
                    candidateSites.Add(s);
                    break;
            }
        }

        Vector3 CenterOf(int room) =>
            MapPos.TryGetRoomCenter(dd, room, out var c) ? c : playerPos;

        // ---- whole-room route ordering ----
        // candidateSites is already the admitted set — the model pool was
        // restricted to the initial-policy + Sight-direct keys at produce
        // time and the fallback produces the fixed per-room set. Required
        // and roomless authoritative work never disappears.
        var selectedCandidates = candidateSites;

        var workByRoom = new Dictionary<int, List<ObjectiveSite>>();
        var roomlessRequired = new List<ObjectiveSite>();
        void AddWork(in ObjectiveSite s)
        {
            if (s.RoomIndex < 0) { roomlessRequired.Add(s); return; }
            if (!workByRoom.TryGetValue(s.RoomIndex, out var list))
                workByRoom[s.RoomIndex] = list = new();
            list.Add(s);
        }
        foreach (var s in requiredSites) AddWork(s);
        foreach (var s in selectedCandidates) AddWork(s);

        var chestByRoom = new Dictionary<int, List<ObjectiveSite>>();
        foreach (var c in chestSites)
            if (c.RoomIndex >= 0)
            {
                if (!chestByRoom.TryGetValue(c.RoomIndex, out var list))
                    chestByRoom[c.RoomIndex] = list = new();
                list.Add(c);
            }

        // Per-room interior preplanned ONCE per rebuild: cheapest
        // insertion evaluated by the expected-search score over the
        // room's work AND ordinary chests; the anchor is the player
        // position in the current room, the room center elsewhere.
        List<ObjectiveSite> OrderRoomItems(int room, List<ObjectiveSite> items)
        {
            var anchor = room == playerRoom ? playerPos : CenterOf(room);
            var order = new List<ObjectiveSite>(items.Count);
            var pool = items.OrderBy(x => x.Key ?? string.Empty,
                StringComparer.Ordinal).ToList();
            foreach (var s in pool)
            {
                var bestPos = order.Count;
                var bestScore = double.MaxValue;
                for (var i = 0; i <= order.Count; i++)
                {
                    var trial = new List<ObjectiveSite>(order.Count + 1);
                    trial.AddRange(order);
                    trial.Insert(i, s);
                    var sc = RoomSitePlanner.ExpectedSearchSeconds(
                        anchor, trial, (a, b) => dist.Meters(a, b), optionalProb);
                    if (sc < bestScore) { bestScore = sc; bestPos = i; }
                }
                order.Insert(bestPos, s);
            }
            return order;
        }
        var interior = new Dictionary<int, List<ObjectiveSite>>();
        List<ObjectiveSite> InteriorFor(int room)
        {
            if (!interior.TryGetValue(room, out var items))
            {
                var pool = new List<ObjectiveSite>();
                if (workByRoom.TryGetValue(room, out var w)) pool.AddRange(w);
                if (chestByRoom.TryGetValue(room, out var ch)) pool.AddRange(ch);
                interior[room] = items = pool.Count > 0
                    ? OrderRoomItems(room, pool) : pool;
            }
            return items;
        }
        var destRooms = new List<int>(workByRoom.Keys);
        destRooms.Sort();

        // Expand a room order along the real RoomDistanceEstimator paths:
        // every visited room is processed at its FIRST traversal —
        // corridor work and chests are served en route, never duplicated.
        List<int> ExpandVisit(IReadOnlyList<int> roomOrder)
        {
            var visit = new List<int>();
            var seen = new HashSet<int>();
            var from = playerRoom;
            foreach (var dest in roomOrder)
            {
                // A destination already served en route is skipped
                // WITHOUT moving the virtual cursor back to it — that
                // would admit chests on a leg the flat route never takes.
                if (seen.Contains(dest))
                    continue;
                var path = roomRoute(from, dest);
                if (path != null)
                    foreach (var r in path)
                        if (seen.Add(r)) visit.Add(r);
                if (seen.Add(dest)) visit.Add(dest);
                from = dest;
            }
            if (playerRoom >= 0 && seen.Add(playerRoom))
                visit.Insert(0, playerRoom);
            return visit;
        }
        List<ObjectiveSite> FlattenVisit(IReadOnlyList<int> visit)
        {
            var flat = new List<ObjectiveSite>();
            foreach (var room in visit)
                flat.AddRange(InteriorFor(room));
            return flat;
        }
        float RouteMeters(IReadOnlyList<ObjectiveSite> flat, Vector3 terminalPos)
        {
            var meters = 0f;
            var prev = playerPos;
            foreach (var s in flat)
            {
                meters += dist.Meters(prev, s.Position);
                prev = s.Position;
            }
            return meters + dist.Meters(prev, terminalPos);
        }
        Vector3 RouteTerminal(List<int> order) =>
            passageOpen ? passagePos
                : (order.Count > 0 ? CenterOf(order[^1]) : playerPos);

        // Destination-room ordering: full permutations up to 6 rooms
        // (720), nearest-room seed + bounded single-room relocations
        // above — computational bounds, not admission limits.
        List<int> bestOrder = new(destRooms);
        List<ObjectiveSite>? bestFlat = null;
        double bestScore = double.MaxValue;
        float bestMeters = float.MaxValue;
        var bestSignature = "";
        void Evaluate(List<int> order, out List<ObjectiveSite> flat,
            out double score, out float meters, out string signature)
        {
            flat = FlattenVisit(ExpandVisit(order));
            score = RoomSitePlanner.ExpectedSearchSeconds(
                playerPos, flat, (a, b) => dist.Meters(a, b), optionalProb);
            meters = RouteMeters(flat, RouteTerminal(order));
            signature = string.Join('>', flat.Select(s => s.Key));
        }
        bool Better(double score, float meters, string signature) =>
            score < bestScore - 1e-9 ||
            (Math.Abs(score - bestScore) <= 1e-9 &&
                (meters < bestMeters - 1e-6f ||
                 (Math.Abs(meters - bestMeters) <= 1e-6f &&
                  string.CompareOrdinal(signature, bestSignature) < 0)));
        void Consider(List<int> order)
        {
            Evaluate(order, out var flat, out var score, out var meters,
                out var signature);
            if (bestFlat == null || Better(score, meters, signature))
            {
                bestScore = score;
                bestMeters = meters;
                bestSignature = signature;
                bestOrder = new List<int>(order);
                bestFlat = flat;
            }
        }
        if (destRooms.Count <= 6)
        {
            void Permute(List<int> items, int k)
            {
                if (k == items.Count) { Consider(items); return; }
                for (var i = k; i < items.Count; i++)
                {
                    (items[k], items[i]) = (items[i], items[k]);
                    Permute(items, k + 1);
                    (items[k], items[i]) = (items[i], items[k]);
                }
            }
            Permute(new List<int>(destRooms), 0);
            if (destRooms.Count == 0)
                Consider(new List<int>());
        }
        else
        {
            var order = new List<int>();
            var rest = new List<int>(destRooms);
            var from = playerRoom;
            while (rest.Count > 0)
            {
                var bi = 0;
                var bd = int.MaxValue;
                for (var i = 0; i < rest.Count; i++)
                {
                    var d = NormalGraphDistance(from, rest[i]);
                    if (d < bd) { bd = d; bi = i; }
                }
                from = rest[bi];
                order.Add(rest[bi]);
                rest.RemoveAt(bi);
            }
            Consider(order);
            var improved = true;
            for (var pass = 0; improved && pass < destRooms.Count; pass++)
            {
                improved = false;
                for (var i = 0; i < order.Count; i++)
                    for (var j = 0; j < order.Count; j++)
                    {
                        if (i == j) continue;
                        var moved = order[i];
                        var trial = new List<int>(order);
                        trial.RemoveAt(i);
                        trial.Insert(j > i ? j - 1 : j, moved);
                        Evaluate(trial, out var flat2, out var sc2,
                            out var m2, out var sig2);
                        if (sc2 < bestScore - 1e-9 ||
                            (Math.Abs(sc2 - bestScore) <= 1e-9 &&
                             m2 < bestMeters - 1e-6f))
                        {
                            order = trial;
                            bestScore = sc2;
                            bestMeters = m2;
                            bestSignature = sig2;
                            bestOrder = new List<int>(trial);
                            bestFlat = flat2;
                            improved = true;
                        }
                    }
            }
        }
        var visit = ExpandVisit(bestOrder);
        var flatWork = bestFlat ?? new List<ObjectiveSite>();

        // Ordinary chests are admitted only on actual routes — they
        // never add a destination. With no work destinations, keep the
        // existing current-room to passage-room admission route; when
        // passage is open, the leg toward it admits chests en route.
        var ordinaryChestKeys = new HashSet<string>();
        foreach (var c in chestSites) ordinaryChestKeys.Add(SiteKey(c));
        var admittedChests = 0;
        foreach (var s in flatWork)
            if (s.Kind == ObjectiveSiteKind.ConfirmedChest &&
                ordinaryChestKeys.Contains(SiteKey(s)))
                admittedChests++;
        if (passageRoom >= 0 && (destRooms.Count == 0 || passageOpen && visit.Count > 0))
        {
            var seen = new HashSet<int>(visit);
            var leg = new List<int>();
            int legStart = destRooms.Count == 0 ? playerRoom : visit[^1];
            foreach (var r in roomRoute(legStart, passageRoom) ??
                new List<int>())
                if (seen.Add(r)) leg.Add(r);
            if (seen.Add(passageRoom)) leg.Add(passageRoom);
            foreach (var room in leg)
            {
                if (!chestByRoom.ContainsKey(room)) continue;
                foreach (var c in InteriorFor(room))
                    if (c.Kind == ObjectiveSiteKind.ConfirmedChest)
                    { flatWork.Add(c); ++admittedChests; }
            }
        }
        flatWork.AddRange(roomlessRequired);

        // HOARD search obligation — separate from ordinary admitted work.
        // Factual terminal states and a discovered coffer resolve it;
        // pending/known-present/exact states never do; in blind search
        // "no selected hoard-oriented work" is a policy conclusion.
        var hoardWorkRemaining = selectedCandidates.Count;
        foreach (var s in requiredSites)
            if (s.Kind is ObjectiveSiteKind.Hoard or ObjectiveSiteKind.HoardIndicator ||
                (s.Kind == ObjectiveSiteKind.RoomProbe &&
                 s.Key.EndsWith(":IntuitionPosition", StringComparison.Ordinal)))
                hoardWorkRemaining++;
        bool unobserved = evidence.Available &&
            (state == HoardEvidenceState.BlindSearch && hoardWorkRemaining == 0 &&
             !blindCoverageGap && fk.CompletedProbeCount > 0 ||
             state == HoardEvidenceState.IntuitionActiveUnconfirmed && _anchors.Count != 0 &&
             _anchors.All(anchor => anchor.Room == spawnRoom ||
                 fk.IsIntelCompleted(anchor.Room, RoomIntelPurpose.IntuitionPosition)));
        bool incompleteBlindSearch = blindCoverageGap && hoardWorkRemaining == 0;
        string? observationResult = unobserved ? "unobserved" : incompleteBlindSearch ? "incomplete" : null;
        if (observationResult != _hoardObservationResult && observationResult != null)
            _record?.Invoke($"hoard-{observationResult}", new { floor = dd->Floor, state,
                completedProbes = fk.CompletedProbeCount, evidence.RefreshSequence });
        _hoardObservationResult = observationResult;
        var hoardResolved =
            fk.HoardAcquired ||
            (policy.DiscoveryOnly && fk.ObservedBandedPositions.Count > 0) ||
            state is HoardEvidenceState.IntuitionNoHoard
                or HoardEvidenceState.Disabled or HoardEvidenceState.AlreadyOpened
                or HoardEvidenceState.FloorsetMaxed
                or HoardEvidenceState.FloorsetDistributionExcluded ||
            unobserved ||
            (state == HoardEvidenceState.BlindSearch && hoardWorkRemaining == 0);
        fk.SearchObligationsResolved = hoardResolved;

        // Useful work = required work + selected candidates + admitted
        // chests — computed after real path/admission, never a baseline
        // guess. Quota exists only to open the way or finish combat when
        // NO useful work remains.
        var useful = requiredSites.Count + selectedCandidates.Count + admittedChests;
        if (useful == 0 && (!passageOpen || inCombatNow) && _filter.HuntMobs &&
            !_failedWorkKeys.ContainsKey("quota"))
            produced.Add(new ObjectiveSite(
                -1, ObjectiveSiteKind.Quota, -1,
                ChasePositionProvider?.Invoke() ?? playerPos,
                ObjectiveSiteSource.RewardOf(ObjectiveSiteKind.Quota, 0), 10f,
                ObjectiveSiteStatus.Open, Key: "quota"));

        // Passage: open, out of combat, hoard resolved, and no required
        // work left — admitted on-route chests may precede it.
        ObjectiveSite? passageSite = null;
        bool readyForPassage = passageOpen && !inCombatNow && hoardResolved &&
            requiredSites.Count == 0 && selectedCandidates.Count == 0;
        PassageSearchExhausted = readyForPassage && useful == 0 && searchExhausted;
        var blockedReason = readyForPassage && useful == 0 && passage == null
            ? PassageSearchExhausted
                ? "已完成地圖標記的出口探查，找不到尚可嘗試的燈籠。"
                : DescribePassageBlock(evidence)
            : !hoardResolved && useful == 0 &&
                (state is HoardEvidenceState.IntuitionDirect or HoardEvidenceState.IntuitionWaitingForIndicator
                    or HoardEvidenceState.IntuitionActiveUnconfirmed or HoardEvidenceState.IntuitionPending)
                ? state == HoardEvidenceState.IntuitionPending ? "等待本層感知回報。" :
                    "找寶工作未完成，等待新的寶藏觀測或可達探查點。"
                : null;
        if (blockedReason != BlockedWorkReason)
        {
            BlockedWorkReason = blockedReason;
            _record?.Invoke("floor-work-block", new { floor = dd->Floor, reason = blockedReason,
                failed = _failedWorkKeys.Select(item => new { item.Key, reason = item.Value.Failure?.ToString() }).ToArray(),
                observedPassagePoints = _observedPassageWork.ToArray() });
        }
        if (readyForPassage && passage is { } goal)
        {
            passageSite = new ObjectiveSite(
                -100, goal.IsSearch ? ObjectiveSiteKind.PassageSearch : ObjectiveSiteKind.Passage,
                goal.Room, goal.Position, ObjectiveSiteSource.RewardOf(ObjectiveSiteKind.Passage, 0), 4f,
                ObjectiveSiteStatus.Open, Key: goal.Key, ActorId: goal.ActorId);
            produced.Add(passageSite.Value);
        }

        _sites.Clear();
        _sites.AddRange(produced);

        // ---- flattened route assembly ----
        _routeWork.Clear();
        foreach (var s in flatWork)
        {
            var key = SiteKey(s);
            _routeWork.Add(new RoomSitePlanner.RouteWorkItem(s, 0f,
                optionalProb.TryGetValue(key, out var wprob) ? wprob : 0d));
        }
        // Admitted on-route chests order before actual passage traversal —
        // the passage site is the terminal item.
        if (passageSite is { } terminal)
            _routeWork.Add(new RoomSitePlanner.RouteWorkItem(terminal, 0f, 0d));

        // Publish effective progress from the SELECTED work — quota and
        // passage are progression-only and never count as pending work.
        fk.PendingWorkCount = useful;
        fk.RequiredWorkRemaining = useful;
        fk.PendingIntelWork = requiredSites.Exists(s => s.Kind == ObjectiveSiteKind.RoomProbe);
        fk.PendingBandedWork = requiredSites.Exists(
            s => s.Kind == ObjectiveSiteKind.ConfirmedChest && s.Chest == FloorChestKind.Banded);
        fk.WorkDecisionInitialized = true;
        _routeRebuilt = true;
        _routeState = state.ToString();
        _routeSource = fk.SelectionSource;
        _routeRate = rate;
        _routeProductive = useful;
        _routePassageOpen = passageOpen;
        _routeResolved = hoardResolved;
        _routeSkipped = "";
        {
            var skipped = new List<string>();
            var routed = new HashSet<string>();
            foreach (var w in _routeWork)
                routed.Add(SiteKey(w.Site));
            foreach (var s in produced)
                if (s.Status == ObjectiveSiteStatus.Open &&
                    s.Kind is not ObjectiveSiteKind.Quota && !routed.Contains(SiteKey(s)))
                    skipped.Add(SiteKey(s));
            _routeSkipped = string.Join(",", skipped);
        }
    }

    private int NormalGraphDistance(int from, int to)
    {
        if (from < 0 || to < 0)
            return 999;
        if (from == to)
            return 0;
        var g = _normalGraph;
        return g != null ? g.RoomDistances[from, to] : 999;
    }

    private string SelectedKeys()
    {
        var keys = new List<string>(_routeWork.Count);
        foreach (var w in _routeWork)
            keys.Add(SiteKey(w.Site));
        return string.Join(",", keys);
    }

    private static bool TryIntelPurpose(string key, out RoomIntelPurpose purpose, out int room)
    {
        purpose = default;
        room = -1;
        if (!key.StartsWith("probe:", StringComparison.Ordinal))
            return false;
        var parts = key.Split(':');
        return parts.Length == 3 && int.TryParse(parts[1], out room) &&
            Enum.TryParse(parts[2], out purpose);
    }

    private static bool TryChestEntity(string key, out ulong entity)
    {
        entity = 0;
        return key.StartsWith("chest:", StringComparison.Ordinal) &&
            ulong.TryParse(key.Substring(6), out entity);
    }

    /// <summary>Typed outcome for the captured work token — the only write
    /// path from execution back to work truth. A late result updates its
    /// original floor work, never a different head or floor.</summary>
    private readonly record struct WorkFailure(ObjectiveSiteKind Kind, FailureKind? Failure,
        FloorObjectEvidence? PassageObservation, NavigationRegionExhaustion? Exhaustion);
    private readonly Dictionary<string, (ChestInteractionAttempt Attempt, bool Blocked)> _chestCapacity = new();
    internal ChestInteractionAttempt? TakeChestOvercapObservation(string key) =>
        _chestCapacity.TryGetValue(key, out var capacity) && !capacity.Blocked &&
        _chestCapacity.Remove(key) ? capacity.Attempt : null;
    private readonly Dictionary<string, WorkFailure> _failedWorkKeys = new(StringComparer.Ordinal);
    private readonly HashSet<string> _observedPassageWork = new(StringComparer.Ordinal);
    private NavBitmap? _navigationMap;
    private int _failureOriginRoom = -1;
    private string? _hoardObservationResult;
    private PassageGoal? _selectedPassage;

    internal (RunFloorRoutePointFact[] Route, string? Unavailable) BuildNonHoardRoute(
        Vector3 origin, int originRoom)
    {
        if (_dist is not { } dist || _selectedPassage is not { } goal)
            return ([], "no reachable passage objective is available");
        if (originRoom < 0 || goal.Room < 0 ||
            (originRoom != goal.Room && dist.RouteBetween(originRoom, goal.Room) == null))
            return ([], "origin and passage objective have no room route");

        var route = new List<RunFloorRoutePointFact>
        {
            new(originRoom, new(origin.X, origin.Y, origin.Z), RunFloorRoutePointKind.Origin)
        };
        var rooms = new List<int> { originRoom };
        foreach (var room in dist.RouteBetween(originRoom, goal.Room) ?? [])
            if (rooms[^1] != room) rooms.Add(room);
        if (rooms[^1] != goal.Room) rooms.Add(goal.Room);
        foreach (var room in rooms)
        {
            var anchor = _anchors.Find(a => a.Room == room).Pos;
            var position = route.Count == 1 && room == originRoom ? origin : anchor;
            var chests = _sites.Where(s => s.Kind == ObjectiveSiteKind.ConfirmedChest &&
                s.Status == ObjectiveSiteStatus.Open && s.RoomIndex == room && s.Chest != FloorChestKind.Banded).ToList();
            while (chests.Count > 0)
            {
                var next = chests.MinBy(c => dist.Meters(position, c.Position));
                chests.Remove(next);
                position = next.Position;
                route.Add(new(room, new(position.X, position.Y, position.Z), RunFloorRoutePointKind.RetainedVisibleChest));
            }
        }
        route.Add(new(goal.Room, new(goal.Position.X, goal.Position.Y, goal.Position.Z), RunFloorRoutePointKind.Passage));
        return (route.ToArray(), null);
    }

    private unsafe PassageGoal? SelectPassageGoal(InstanceContentDeepDungeon* dd,
        FloorObjectEvidenceSnapshot evidence, Vector3 player, RoomSitePlanner.RoomDistanceEstimator distance,
        out bool exhausted)
    {
        exhausted = false;
        var beacons = new List<PassageBeacon>(evidence.PassageActors.Count);
        foreach (var actor in evidence.PassageActors)
        {
            var key = $"pass:{actor.GameObjectId}";
            if (_failedWorkKeys.TryGetValue(key, out var failure))
            {
                bool changed = failure.PassageObservation is not { } previous ||
                    previous.Position != actor.Position || previous.SharedTimelineState != actor.SharedTimelineState;
                if (changed && NativePassageAvailability.IsAvailable(actor.BaseId, actor.SharedTimelineState) ||
                    failure.Failure == FailureKind.PassageUnobserved &&
                    NativePassageAvailability.IsRemoved(actor.BaseId, actor.SharedTimelineState))
                    _failedWorkKeys.Remove(key);
            }
            beacons.Add(new(actor.GameObjectId, actor.BaseId, actor.SharedTimelineState,
                distance.RoomOf(actor.Position), actor.Position));
        }

        var points = new List<PassageSearchPoint>();
        var flags = dd->MapData;
        int mappedPassageRooms = 0;
        foreach (var (room, center) in _anchors)
            if ((flags[room] & InstanceContentDeepDungeon.RoomFlags.Passage) != 0)
            {
                mappedPassageRooms++;
                points.Add(new(room, center));
                if (_navigationMap != null)
                    foreach (var point in _navigationMap.PassageObservationPoints)
                        if (point.Room == room) points.Add(point);
            }

        ulong committed = CommitProtected && Head is { Kind: ObjectiveSiteKind.Passage } head ? head.ActorId : 0;
        if (committed != 0 && _selectedPassage is { } prior && prior.ActorId == committed &&
            !evidence.PassageActors.Any(a => a.GameObjectId == committed))
            return prior;
        var selected = PassageGoalPolicy.Select(beacons, points,
            point => distance.Meters(player, point),
            goal => !_failedWorkKeys.ContainsKey(goal.Key) && !_observedPassageWork.Contains(goal.Key),
            committed);
        if (selected != null || !_activity.PassageOpen || !evidence.Available ||
            evidence.ScannedObjectCount == 0 || mappedPassageRooms == 0) return selected;

        // A failed approach is not a completed scan. Verify coverage against the
        // native flags so an omitted room or a transitional actor cannot stop the run.
        int markedRooms = 0;
        foreach (var roomFlags in flags)
            if ((roomFlags & InstanceContentDeepDungeon.RoomFlags.Passage) != 0) markedRooms++;
        exhausted = markedRooms == mappedPassageRooms &&
            !_failedWorkKeys.Values.Any(failed => failed.Kind == ObjectiveSiteKind.Passage &&
                failed.Failure == FailureKind.PassageUnobserved) &&
            points.All(point => _observedPassageWork.Contains(new PassageGoal(point.Room, point.Position, SearchDoor: point.Door).Key)) &&
            beacons.All(beacon => NativePassageAvailability.IsRemoved(beacon.BaseId, beacon.State) ||
                NativePassageAvailability.IsAvailable(beacon.BaseId, beacon.State) &&
                _failedWorkKeys.TryGetValue(new PassageGoal(beacon.Room, beacon.Position, beacon.ActorId).Key, out var failed) &&
                failed.Failure is FailureKind.PassageNoTransfer or FailureKind.Unreachable);
        return null;
    }

    private void ExcludeFailedWork(List<ObjectiveSite> sites, Vector3 player)
    {
        if (_failedWorkKeys.Count == 0) return;
        float offset = CombatPositionNavigation.CurrentYOffset;
        sites.RemoveAll(site =>
        {
            var key = SiteKey(site);
            if (!_failedWorkKeys.TryGetValue(key, out var failure)) return false;
            if (failure.Exhaustion is not { } exhausted ||
                exhausted.AppliesTo(_navigationMap, player, site.Position, offset))
                return true;
            _failedWorkKeys.Remove(key);
            return false;
        });
    }

    private string DescribePassageBlock(FloorObjectEvidenceSnapshot evidence)
    {
        if (!evidence.Available) return "等待出口物件觀測。";
        if (_failedWorkKeys.Any(entry => entry.Value.Kind == ObjectiveSiteKind.Passage && entry.Value.Failure == FailureKind.PassageNoTransfer))
            return "已踩過出口，但未確認轉層。等待出口狀態更新。";
        if (_failedWorkKeys.Any(entry => entry.Value.Kind is (ObjectiveSiteKind.Passage or ObjectiveSiteKind.PassageSearch) &&
                entry.Value.Failure == FailureKind.Unreachable))
            return "出口或探查點不可達。等待新的可達位置或地形。";
        return _observedPassageWork.Count > 0
            ? "已觀測指定出口區域，等待可用燈籠出現。"
            : "尚未觀測到可用燈籠或出口探查位置。";
    }

    private void RejectWorkKey(string key, WorkFailure failure)
    {
        _failedWorkKeys[key] = failure;
        _sites.RemoveAll(s => SiteKey(s) == key);
        _routeWork.RemoveAll(w => SiteKey(w.Site) == key);
    }

    internal void ReportWorkOutcome(SiteWorkToken token, ObjectiveSite site, SiteWorkOutcome outcome, FailureKind? failure,
        FloorObjectEvidenceSnapshot? evidence, ChestInteractionAttempt? chest, NavigationRegionExhaustion? exhaustion)
    {
        var fk = _knowledge?.Floor;
        if (fk == null || fk.Generation != token.FloorGeneration)
            return;
        if (!_attempts.TryGetValue(token.Key, out var latest) || latest != token.Attempt)
            return;
        for (var i = 0; i < _sites.Count; i++)
            if (SiteKey(_sites[i]) == token.Key)
            {
                var s = _sites[i];
                var status = outcome switch
                {
                    SiteWorkOutcome.Completed => ObjectiveSiteStatus.Done,
                    // Non-completion outcomes never establish completion;
                    // Failed work stays excluded until its blocking
                    // observation changes.
                    _ => ObjectiveSiteStatus.Open,
                };
                _sites[i] = s with { Status = status, Reason = failure?.ToString() };
                break;
            }
        if (outcome == SiteWorkOutcome.Completed)
        {
            if (site.Kind == ObjectiveSiteKind.PassageSearch)
            {
                if (evidence?.PlayerPosition is { } position)
                {
                    _observedPassageWork.Add(token.Key);
                    _record?.Invoke("passage-area-observed", new { floor = fk.Floor, token.Key,
                        sequence = evidence.RefreshSequence, position });
                }
            }
            else if (site.Kind == ObjectiveSiteKind.Candidate)
                fk.MarkProbeCompleted(token.Key);
            else if (TryIntelPurpose(token.Key, out var purpose, out var room))
                fk.MarkIntelCompleted(room, purpose);
            else if (TryChestEntity(token.Key, out var entity))
                fk.MarkChestCompleted(entity);
            // Hoard acquisition itself is the native count delta —
            // standing on the point is never "acquired".
        }
        // Keep the observed full item so the next decision can check capacity
        // and current item eligibility without interacting again.
        if (outcome == SiteWorkOutcome.PolicySkipped && site.Kind == ObjectiveSiteKind.ConfirmedChest &&
            chest is { PendingGoldOvercapSlotIndex: not null } or { PendingSilverOvercapStoneRowId: not null })
            _chestCapacity[token.Key] = (chest, true);
        // A failed attempt establishes that this observation did not
        // produce a route or result. It does not establish completion.
        if (outcome == SiteWorkOutcome.Failed)
        {
            FloorObjectEvidence? observation = null;
            if (site.Kind == ObjectiveSiteKind.Passage && evidence?.Available == true)
                foreach (var actor in evidence.PassageActors)
                    if (actor.GameObjectId == site.ActorId) { observation = actor; break; }
            RejectWorkKey(token.Key, new(site.Kind, failure, observation, exhaustion));
        }
        _routeDirty = true;
        if (Head is { } h && SiteKey(h) == token.Key)
        {
            ClearCommit();
            CommitProtected = false;
        }
    }

    public void Dispose()
    {
        if (Active == this)
            Active = null;
        _sites.Clear();
        _routeWork.Clear();
        _chestCapacity.Clear();
        _failedWorkKeys.Clear();
        Head = null;
        _committedKey = "";
        _committedToken = default;
        Status = "disposed";
    }
}
