using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using DeepDungeon.Fsd.Core;
using DeepDungeon.Fsd.Dalamud.GameState;
using DeepDungeon.Fsd.Dalamud.Runtime.Search;
using FFXIVClientStructs.FFXIV.Client.Game.InstanceContent;

namespace DeepDungeon.Fsd.Dalamud.Runtime.Floor;

public sealed partial class FloorPhaseController
{
    private unsafe void PublishInitialRunFloorState(
        InstanceContentDeepDungeon* dd,
        Vector3 origin)
    {
        FloorRuntime? runtime = _floorRuntime;
        if (_runTelemetryObserver == null ||
            runtime is not { IsDisposed: false, Kind: FloorRuntimeKind.Normal } ||
            runtime.RunFloorStatePublisher != null ||
            _ctx?.ControlledPtSurvey != null ||
            runtime.NormalGraph == null ||
            runtime.ObjectEvidence.Current?.Available != true)
        {
            return;
        }

        RunFloorStateTelemetry state = BuildRunFloorState(dd, runtime, origin);
        runtime.RunFloorStatePublisher = new RunFloorStateCumulativePublisher(state);
        NotifyRunFloorState(runtime.RunFloorStatePublisher.LastPublished);
    }

    private unsafe void PublishAuthoritativeRunFloorStateIfChanged(
        InstanceContentDeepDungeon* dd,
        FloorRuntime runtime)
    {
        RunFloorStateCumulativePublisher? publisher = runtime.RunFloorStatePublisher;
        if (_runTelemetryObserver == null || publisher == null ||
            runtime.IsDisposed || runtime.Kind != FloorRuntimeKind.Normal ||
            runtime.ObjectEvidence.Current?.Available != true)
        {
            return;
        }

        ResolveAuthoritativeHoardFacts(
            runtime,
            out RawWorldPosition? exact,
            out RawWorldPosition? banded);
        RawWorldPosition[] observedSightTraps =
            (runtime.Executor?.ObservedSightTrapPositions ?? Array.Empty<Vector3>())
            .Select(ToRawPosition)
            .ToArray();
        FloorsetHoardOpportunity floorsetHoardOpportunity =
            DeepDungeonFloorsetTracker.GetCurrentOpportunity(runtime.Floor);
        RunFloorStateTelemetry? state = publisher.PublishFactual(
            DateTime.UtcNow,
            floorsetHoardOpportunity,
            runtime.Executor?.HasOpenedHoardThisFloor == true,
            exact,
            banded,
            observedSightTraps,
            (lastPublished, currentSight) =>
            {
                DetailedMapRunSnapshot runMap = _ctx?.DetailedMap ??
                    throw new InvalidOperationException(
                        "A factual floor update requires its run map snapshot.");
                return BuildCandidateFacts(
                    lastPublished.ActiveLayoutIndex,
                    lastPublished.Floor,
                    lastPublished.Rooms,
                    currentSight,
                    runMap.Catalog,
                    runMap.HoardYield);
            });
        if (state != null)
            NotifyRunFloorState(state);
    }

    private unsafe RunFloorStateTelemetry BuildRunFloorState(
        InstanceContentDeepDungeon* dd,
        FloorRuntime runtime,
        Vector3 origin)
    {
        NormalFloorGraphSnapshot graph = runtime.NormalGraph ??
            throw new InvalidOperationException("Normal floor state requires a room graph.");
        FloorRoomBinding[] bindings = FloorEvidenceSession.BuildRoomBindings(
            dd,
            graph.ReachableRooms);
        RunFloorRoomFact[] rooms = bindings
            .OrderBy(binding => binding.RoomIndex)
            .Select(binding => new RunFloorRoomFact(
                binding.RoomIndex,
                binding.ConnectionFlags,
                binding.RoomCenter))
            .ToArray();
        var edges = new List<RunFloorRoomEdge>();
        for (int left = 0; left < graph.ReachableRooms.Count; left++)
        {
            for (int right = left + 1; right < graph.ReachableRooms.Count; right++)
            {
                int leftRoom = graph.ReachableRooms[left];
                int rightRoom = graph.ReachableRooms[right];
                if (graph.RoomDistances[leftRoom, rightRoom] == 1)
                    edges.Add(new RunFloorRoomEdge(leftRoom, rightRoom));
            }
        }

        DetailedMapRunSnapshot runMap = _ctx?.DetailedMap ??
            throw new InvalidOperationException("Normal floor state requires its run map snapshot.");
        DetailedMapCatalog? catalog = runMap.Catalog;
        HoardYieldCatalog? yield = runMap.HoardYield;
        FloorsetHoardOpportunity floorsetHoardOpportunity =
            DeepDungeonFloorsetTracker.GetCurrentOpportunity(runtime.Floor);
        RawWorldPosition[] observedSightTraps =
            (runtime.Executor?.ObservedSightTrapPositions ?? Array.Empty<Vector3>())
            .Select(ToRawPosition)
            .ToArray();
        RunFloorCandidateFact[] candidates = BuildCandidateFacts(
            dd->ActiveLayoutIndex,
            runtime.Floor,
            rooms,
            observedSightTraps,
            catalog,
            yield);
        double? hoardExistsProbability = yield?.FloorEstimates
            .SingleOrDefault(estimate => estimate.Floor == runtime.Floor)
            ?.EstimatedHoardProbability;
        ResolveAuthoritativeHoardFacts(
            runtime,
            out RawWorldPosition? exact,
            out RawWorldPosition? banded);
        RunFloorVisibleChestFact[] visibleChests =
            BuildVisibleChestFacts(runtime, rooms);
        BuildRetainedRouteFacts(
            dd,
            runtime,
            origin,
            rooms,
            visibleChests,
            out RunFloorRoutePointFact[] retainedRoute,
            out string? retainedRouteUnavailableReason);

        return new RunFloorStateTelemetry
        {
            TimestampUtc = DateTime.UtcNow,
            Trigger = RunFloorStateTrigger.StableSetup,
            JobId = Service.LocalPlayer?.ClassJob.RowId ?? 0,
            DungeonId = runtime.DungeonId,
            TerritoryId = Service.ClientState.TerritoryType,
            FloorsetStart = ((runtime.Floor - 1) / 10) * 10 + 1,
            Floor = runtime.Floor,
            FloorGeneration = runtime.Generation,
            ControlledSurvey = false,
            ActiveLayoutIndex = dd->ActiveLayoutIndex,
            DetailedMapActive = runMap.Policy == DetailedMapRuntimePolicy.DetailedMap,
            YieldAvailable = yield != null,
            FloorsetHoardOpportunity = floorsetHoardOpportunity,
            HoardOpenedThisFloor = runtime.Executor?.HasOpenedHoardThisFloor == true,
            CatalogReleaseId = catalog?.ReleaseId,
            CatalogModelSha256 = catalog?.ModelSha256,
            HoardYieldSha256 = catalog?.HoardYieldSha256,
            HoardExistsProbability = hoardExistsProbability,
            OriginRoomIndex = RoomGraph.GetLocalPlayerRoomIndex(dd),
            Origin = ToRawPosition(origin),
            Rooms = rooms,
            RoomEdges = edges.ToArray(),
            Candidates = candidates,
            ObservedSightTraps = observedSightTraps,
            ExactHoardIndicator = exact,
            VisibleBanded = banded,
            VisibleChests = visibleChests,
            RetainedRoute = retainedRoute,
            RetainedRouteUnavailableReason = retainedRouteUnavailableReason
        };
    }

    private static unsafe void BuildRetainedRouteFacts(
        InstanceContentDeepDungeon* dd,
        FloorRuntime runtime,
        Vector3 origin,
        IReadOnlyList<RunFloorRoomFact> rooms,
        IReadOnlyList<RunFloorVisibleChestFact> visibleChests,
        out RunFloorRoutePointFact[] route,
        out string? unavailableReason)
    {
        route = [];
        unavailableReason = null;
        AutoPilotExecutor? executor = runtime.Executor;
        if (executor?.HasPlanningSnapshot != true)
        {
            unavailableReason = "normal planning snapshot is unavailable";
            return;
        }

        int originRoom = RoomGraph.GetLocalPlayerRoomIndex(dd);
        int passageRoom = RoomGraph.GetPassageRoomIndex(dd);
        if (!TryFindRoom(rooms, originRoom, out _) ||
            !TryFindRoom(rooms, passageRoom, out RunFloorRoomFact passage))
        {
            unavailableReason =
                "origin or passage room is absent from generated room bindings";
            return;
        }

        for (int chestIndex = 0; chestIndex < visibleChests.Count; chestIndex++)
        {
            RunFloorVisibleChestFact chest = visibleChests[chestIndex];
            if (IsChestRetained(chest.Kind, executor.ConfigSnapshot) &&
                !TryFindRoom(rooms, chest.RoomIndex, out _))
            {
                unavailableReason =
                    "a retained visible chest is absent from generated room bindings";
                return;
            }
        }

        var result = new List<RunFloorRoutePointFact>(
            executor.RetainedNonHoardRoute.Count + visibleChests.Count + 2)
        {
            new(
                originRoom,
                ToRawPosition(origin),
                RunFloorRoutePointKind.Origin)
        };
        IReadOnlyList<RoomPlanEntry> retained =
            executor.SnapshotRetainedNonHoardRoute();
        for (int planIndex = 0; planIndex < retained.Count; planIndex++)
        {
            RoomPlanEntry entry = retained[planIndex];
            if (!entry.ShouldSearchChests)
                continue;
            if (!TryFindRoom(rooms, entry.RoomIndex, out RunFloorRoomFact room))
            {
                unavailableReason =
                    $"retained chest room {entry.RoomIndex} is absent from generated room bindings";
                return;
            }

            result.Add(new RunFloorRoutePointFact(
                room.RoomIndex,
                room.Center,
                RunFloorRoutePointKind.RetainedChestRoom));
            AppendRetainedVisibleChests(
                result,
                room.RoomIndex,
                visibleChests,
                executor.ConfigSnapshot);
        }

        result.Add(new RunFloorRoutePointFact(
            passage.RoomIndex,
            passage.Center,
            RunFloorRoutePointKind.Passage));
        route = result.ToArray();
    }

    private static void AppendRetainedVisibleChests(
        List<RunFloorRoutePointFact> route,
        int roomIndex,
        IReadOnlyList<RunFloorVisibleChestFact> visibleChests,
        RunOptions options)
    {
        var remaining = new List<RunFloorVisibleChestFact>();
        for (int index = 0; index < visibleChests.Count; index++)
        {
            RunFloorVisibleChestFact chest = visibleChests[index];
            if (chest.RoomIndex == roomIndex && IsChestRetained(chest.Kind, options))
                remaining.Add(chest);
        }

        RawWorldPosition current = route[^1].Position;
        while (remaining.Count > 0)
        {
            int bestIndex = 0;
            double bestDistance = double.MaxValue;
            for (int index = 0; index < remaining.Count; index++)
            {
                double dx = current.X - remaining[index].Position.X;
                double dz = current.Z - remaining[index].Position.Z;
                double distance = dx * dx + dz * dz;
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    bestIndex = index;
                }
            }
            RunFloorVisibleChestFact next = remaining[bestIndex];
            route.Add(new RunFloorRoutePointFact(
                roomIndex,
                next.Position,
                RunFloorRoutePointKind.RetainedVisibleChest));
            current = next.Position;
            remaining.RemoveAt(bestIndex);
        }
    }

    private static bool IsChestRetained(
        RunFloorVisibleChestKind kind,
        RunOptions options) =>
        kind switch
        {
            RunFloorVisibleChestKind.Bronze => options.OpenBronze,
            RunFloorVisibleChestKind.Silver => options.OpenSilver,
            RunFloorVisibleChestKind.Gold => options.OpenGold,
            _ => false
        };

    private static bool TryFindRoom(
        IReadOnlyList<RunFloorRoomFact> rooms,
        int roomIndex,
        out RunFloorRoomFact room)
    {
        for (int index = 0; index < rooms.Count; index++)
        {
            if (rooms[index].RoomIndex == roomIndex)
            {
                room = rooms[index];
                return true;
            }
        }
        room = default;
        return false;
    }

    internal static RunFloorCandidateFact[] BuildCandidateFacts(
        int layoutIndex,
        byte floor,
        IReadOnlyList<RunFloorRoomFact> rooms,
        IReadOnlyList<RawWorldPosition> observedSightTraps,
        DetailedMapCatalog? catalog,
        HoardYieldCatalog? yield)
    {
        if (catalog == null || yield == null)
            return [];
        int floorIndex = Array.IndexOf(yield.Floors, floor);
        if (floorIndex < 0)
            return [];

        var facts = new List<RunFloorCandidateFact>();
        for (int roomIndex = 0; roomIndex < rooms.Count; roomIndex++)
        {
            RunFloorRoomFact room = rooms[roomIndex];
            if (!yield.TryGetRoom(layoutIndex, room.RoomIndex, out HoardYieldRoom yieldRoom))
                continue;
            catalog.TryGetRoom(
                layoutIndex,
                room.RoomIndex,
                out DetailedMapCatalogRoom catalogRoom);
            RawWorldPosition[] directTargets = BuildDirectSuccessorTargets(
                catalogRoom,
                observedSightTraps);
            bool hasDirectTargets = directTargets.Length > 0;
            for (int candidateIndex = 0; candidateIndex < yieldRoom.Candidates.Length; candidateIndex++)
            {
                HoardYieldCandidate candidate = yieldRoom.Candidates[candidateIndex];
                bool isDirectTarget = directTargets.Any(target =>
                    RawWorldPosition.CanonicallyEquals(target, candidate.Position));
                if (hasDirectTargets && !isDirectTarget)
                    continue;
                if (facts.Any(existing =>
                        existing.RoomIndex == room.RoomIndex &&
                        RawWorldPosition.CanonicallyEquals(
                            existing.Position,
                            candidate.Position)))
                {
                    continue;
                }
                facts.Add(new RunFloorCandidateFact(
                    room.RoomIndex,
                    candidate.Position,
                    candidate.Floors[floorIndex].PosteriorWeight,
                    isDirectTarget));
            }
        }
        return facts.ToArray();
    }

    private static RawWorldPosition[] BuildDirectSuccessorTargets(
        DetailedMapCatalogRoom? room,
        IReadOnlyList<RawWorldPosition> observedSightTraps)
    {
        if (room == null)
            return [];
        var targets = new List<RawWorldPosition>();
        for (int trapIndex = 0; trapIndex < observedSightTraps.Count; trapIndex++)
        {
            int sourceIndex = DetailedMapRoomCandidatePlanner.FindUniqueCandidate(
                room.Candidates,
                observedSightTraps[trapIndex]);
            if (sourceIndex < 0)
                continue;
            DetailedMapCatalogSuccessor successor =
                room.Candidates[sourceIndex].Successor;
            if (successor.State != DetailedMapSuccessorState.ObservedUnique ||
                !successor.Target.HasValue)
            {
                continue;
            }
            if (!targets.Any(existing =>
                    RawWorldPosition.CanonicallyEquals(
                        existing,
                        successor.Target.Value)))
            {
                targets.Add(successor.Target.Value);
            }
        }
        return targets.ToArray();
    }

    private static RunFloorVisibleChestFact[] BuildVisibleChestFacts(
        FloorRuntime runtime,
        IReadOnlyList<RunFloorRoomFact> rooms)
    {
        FloorObjectEvidenceSnapshot? evidence = runtime.ObjectEvidence.Current;
        if (evidence?.Available != true)
            return [];
        var facts = new List<RunFloorVisibleChestFact>();
        for (int chestIndex = 0; chestIndex < evidence.Chests.Count; chestIndex++)
        {
            FloorChestEvidence chest = evidence.Chests[chestIndex];
            if (!chest.Object.IsTargetable || chest.Kind == FloorChestKind.Banded)
                continue;
            RunFloorVisibleChestKind kind = chest.Kind switch
            {
                FloorChestKind.Bronze => RunFloorVisibleChestKind.Bronze,
                FloorChestKind.Silver => RunFloorVisibleChestKind.Silver,
                FloorChestKind.Gold => RunFloorVisibleChestKind.Gold,
                _ => throw new InvalidOperationException("Unsupported visible chest kind.")
            };
            RawWorldPosition position = ToRawPosition(chest.Object.Position);
            facts.Add(new RunFloorVisibleChestFact(
                FindContainingRoom(rooms, position),
                position,
                kind));
        }
        return facts.ToArray();
    }

    private static int FindContainingRoom(
        IReadOnlyList<RunFloorRoomFact> rooms,
        in RawWorldPosition position)
    {
        const double roomRadiusSquared = 30d * 30d;
        int result = -1;
        double best = double.MaxValue;
        for (int index = 0; index < rooms.Count; index++)
        {
            RawWorldPosition center = rooms[index].Center;
            double dx = center.X - position.X;
            double dz = center.Z - position.Z;
            double distance = dx * dx + dz * dz;
            if (distance <= roomRadiusSquared && distance < best)
            {
                best = distance;
                result = rooms[index].RoomIndex;
            }
        }
        return result;
    }

    private static void ResolveAuthoritativeHoardFacts(
        FloorRuntime runtime,
        out RawWorldPosition? exact,
        out RawWorldPosition? banded)
    {
        FloorObjectEvidenceSnapshot? evidence = runtime.ObjectEvidence.Current;
        Vector3? exactPosition = runtime.Executor?.CachedHoardIndicatorPos;
        if (!exactPosition.HasValue && evidence?.HoardIndicators.Count > 0)
            exactPosition = evidence.HoardIndicators[0].Object.Position;
        Vector3? bandedPosition = null;
        if (evidence != null)
            BandedChestLocator.TryFindNearestToPlayer(evidence, out bandedPosition);
        exact = exactPosition.HasValue ? ToRawPosition(exactPosition.Value) : null;
        banded = bandedPosition.HasValue ? ToRawPosition(bandedPosition.Value) : null;
    }

    private void NotifyRunFloorState(RunFloorStateTelemetry state)
    {
        try
        {
            _runTelemetryObserver?.ObserveFloorState(state);
        }
        catch (Exception ex)
        {
            Service.Log.Error($"[RunTelemetry] Host floor-state observer failed: {ex}");
        }
    }

    private static RawWorldPosition ToRawPosition(Vector3 position) =>
        new(position.X, position.Y, position.Z);

    internal static bool CanonicalPositionSetsEqual(
        IReadOnlyList<RawWorldPosition> left,
        IReadOnlyList<RawWorldPosition> right)
    {
        if (left.Count != right.Count)
            return false;
        for (int leftIndex = 0; leftIndex < left.Count; leftIndex++)
        {
            bool found = false;
            for (int rightIndex = 0; rightIndex < right.Count; rightIndex++)
            {
                if (RawWorldPosition.CanonicallyEquals(
                        left[leftIndex],
                        right[rightIndex]))
                {
                    found = true;
                    break;
                }
            }
            if (!found)
                return false;
        }
        return true;
    }
}

internal sealed class RunFloorStateCumulativePublisher
{
    public RunFloorStateCumulativePublisher(RunFloorStateTelemetry initial)
    {
        ArgumentNullException.ThrowIfNull(initial);
        if (initial.Trigger != RunFloorStateTrigger.StableSetup)
        {
            throw new InvalidDataException(
                "A cumulative floor-state publisher must begin from stable setup.");
        }

        LastPublished = CloneState(initial);
    }

    public RunFloorStateTelemetry LastPublished { get; private set; }

    public RunFloorStateTelemetry? PublishFactual(
        DateTime timestampUtc,
        FloorsetHoardOpportunity floorsetHoardOpportunity,
        bool hoardOpenedThisFloor,
        RawWorldPosition? exactHoardIndicator,
        RawWorldPosition? visibleBanded,
        IReadOnlyList<RawWorldPosition> observedSightTraps,
        Func<RunFloorStateTelemetry, RawWorldPosition[], RunFloorCandidateFact[]> rebuildCandidates)
    {
        ArgumentNullException.ThrowIfNull(observedSightTraps);
        ArgumentNullException.ThrowIfNull(rebuildCandidates);

        RunFloorStateTelemetry previous = LastPublished;
        RawWorldPosition[] currentSight = observedSightTraps.ToArray();
        bool exactChanged = exactHoardIndicator.HasValue &&
            (!previous.ExactHoardIndicator.HasValue ||
             !RawWorldPosition.CanonicallyEquals(
                 previous.ExactHoardIndicator.Value,
                 exactHoardIndicator.Value));
        bool bandedChanged = visibleBanded.HasValue &&
            (!previous.VisibleBanded.HasValue ||
             !RawWorldPosition.CanonicallyEquals(
                 previous.VisibleBanded.Value,
                 visibleBanded.Value));
        bool sightChanged = !FloorPhaseController.CanonicalPositionSetsEqual(
            previous.ObservedSightTraps,
            currentSight);
        bool floorsetOpportunityChanged =
            previous.FloorsetHoardOpportunity != floorsetHoardOpportunity;
        bool openedChanged = hoardOpenedThisFloor &&
            !previous.HoardOpenedThisFloor;
        bool cumulativeOpened = previous.HoardOpenedThisFloor || hoardOpenedThisFloor;
        if (!exactChanged && !bandedChanged && !sightChanged &&
            !floorsetOpportunityChanged && !openedChanged)
        {
            return null;
        }

        RunFloorCandidateFact[] candidates = previous.Candidates;
        if (sightChanged)
        {
            candidates = rebuildCandidates(previous, currentSight) ??
                throw new InvalidOperationException(
                    "A Sight factual refresh returned no candidate snapshot.");
        }

        RunFloorStateTelemetry published = new()
        {
            TimestampUtc = timestampUtc,
            Trigger = RunFloorStateTrigger.FactualChanged,
            JobId = previous.JobId,
            DungeonId = previous.DungeonId,
            TerritoryId = previous.TerritoryId,
            FloorsetStart = previous.FloorsetStart,
            Floor = previous.Floor,
            FloorGeneration = previous.FloorGeneration,
            ControlledSurvey = previous.ControlledSurvey,
            ActiveLayoutIndex = previous.ActiveLayoutIndex,
            DetailedMapActive = previous.DetailedMapActive,
            YieldAvailable = previous.YieldAvailable,
            FloorsetHoardOpportunity = floorsetHoardOpportunity,
            HoardOpenedThisFloor = cumulativeOpened,
            CatalogReleaseId = previous.CatalogReleaseId,
            CatalogModelSha256 = previous.CatalogModelSha256,
            HoardYieldSha256 = previous.HoardYieldSha256,
            HoardExistsProbability = previous.HoardExistsProbability,
            OriginRoomIndex = previous.OriginRoomIndex,
            Origin = previous.Origin,
            Rooms = previous.Rooms,
            RoomEdges = previous.RoomEdges,
            Candidates = candidates,
            ObservedSightTraps = currentSight,
            ExactHoardIndicator = exactHoardIndicator ?? previous.ExactHoardIndicator,
            VisibleBanded = visibleBanded ?? previous.VisibleBanded,
            VisibleChests = previous.VisibleChests,
            RetainedRoute = previous.RetainedRoute,
            RetainedRouteUnavailableReason = previous.RetainedRouteUnavailableReason
        };
        LastPublished = CloneState(published);
        return published;
    }

    private static RunFloorStateTelemetry CloneState(RunFloorStateTelemetry source) =>
        new()
        {
            TimestampUtc = source.TimestampUtc,
            Trigger = source.Trigger,
            JobId = source.JobId,
            DungeonId = source.DungeonId,
            TerritoryId = source.TerritoryId,
            FloorsetStart = source.FloorsetStart,
            Floor = source.Floor,
            FloorGeneration = source.FloorGeneration,
            ControlledSurvey = source.ControlledSurvey,
            ActiveLayoutIndex = source.ActiveLayoutIndex,
            DetailedMapActive = source.DetailedMapActive,
            YieldAvailable = source.YieldAvailable,
            FloorsetHoardOpportunity = source.FloorsetHoardOpportunity,
            HoardOpenedThisFloor = source.HoardOpenedThisFloor,
            CatalogReleaseId = source.CatalogReleaseId,
            CatalogModelSha256 = source.CatalogModelSha256,
            HoardYieldSha256 = source.HoardYieldSha256,
            HoardExistsProbability = source.HoardExistsProbability,
            OriginRoomIndex = source.OriginRoomIndex,
            Origin = source.Origin,
            Rooms = source.Rooms.ToArray(),
            RoomEdges = source.RoomEdges.ToArray(),
            Candidates = source.Candidates.ToArray(),
            ObservedSightTraps = source.ObservedSightTraps.ToArray(),
            ExactHoardIndicator = source.ExactHoardIndicator,
            VisibleBanded = source.VisibleBanded,
            VisibleChests = source.VisibleChests.ToArray(),
            RetainedRoute = source.RetainedRoute.ToArray(),
            RetainedRouteUnavailableReason = source.RetainedRouteUnavailableReason
        };
}
