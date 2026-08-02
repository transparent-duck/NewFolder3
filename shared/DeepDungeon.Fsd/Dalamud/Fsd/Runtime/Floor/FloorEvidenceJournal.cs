using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using System.Threading.Tasks;
using DeepDungeon.Fsd.Core;
using DeepDungeon.Fsd.Dalamud.GameState;
using DeepDungeon.Fsd.Dalamud.Map;
using FFXIVClientStructs.FFXIV.Client.Game.InstanceContent;
using OmenTools.OmenService;

namespace DeepDungeon.Fsd.Dalamud.Runtime.Floor;

internal sealed class FloorEvidenceJournal : IDisposable
{
    private const string JournalDirectoryName = "DeepDungeonResearch";
    private const string JournalFileName = "floor-evidence-v3.jsonl";
    private readonly Channel<PendingWrite> _pending =
        Channel.CreateUnbounded<PendingWrite>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true,
            AllowSynchronousContinuations = false
        });
    private readonly Task _writerTask;
    private readonly IFloorEvidenceObserver? _observer;
    private bool _disposed;

    public FloorEvidenceJournal(IFloorEvidenceObserver? observer = null)
    {
        _observer = observer;
        string baseDirectory = Service.PluginInterface.GetPluginConfigDirectory();
        string journalDirectory = Path.Combine(baseDirectory, JournalDirectoryName);
        string journalPath = Path.Combine(journalDirectory, JournalFileName);
        _writerTask = Task.Run(() => WriteLoopAsync(journalDirectory, journalPath));
        FilePath = journalPath;
    }

    public string FilePath { get; }

    public void Enqueue(FloorEvidenceBundle bundle)
    {
        if (_disposed)
        {
            Service.Log.Error($"[FloorEvidenceJournal] Rejected finalized floor {bundle.FloorInstanceId}: journal is disposed.");
            return;
        }

        if (!_pending.Writer.TryWrite(new PendingWrite(bundle, null)))
            Service.Log.Error($"[FloorEvidenceJournal] Failed to queue finalized floor {bundle.FloorInstanceId}.");
    }

    public bool EnqueueAndWait(FloorEvidenceBundle bundle, TimeSpan timeout)
    {
        if (_disposed)
            return false;

        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.Writer.TryWrite(new PendingWrite(bundle, completion)))
            return false;
        return completion.Task.Wait(timeout) && completion.Task.Result;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _pending.Writer.TryComplete();
        try
        {
            _writerTask.GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Service.Log.Error($"[FloorEvidenceJournal] Writer shutdown failed: {ex}");
        }
    }

    private async Task WriteLoopAsync(string journalDirectory, string journalPath)
    {
        try
        {
            Directory.CreateDirectory(journalDirectory);
            var options = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
            };
            await using var stream = new FileStream(
                journalPath,
                FileMode.Append,
                FileAccess.Write,
                FileShare.Read,
                16 * 1024,
                FileOptions.Asynchronous);
            await using var writer = new StreamWriter(stream, new UTF8Encoding(false));
            await foreach (var pending in _pending.Reader.ReadAllAsync())
            {
                try
                {
                    string json = JsonSerializer.Serialize(pending.Bundle, options);
                    await writer.WriteLineAsync(json);
                    await writer.FlushAsync();
                    NotifyObserver(pending.Bundle);
                    pending.Completion?.TrySetResult(true);
                }
                catch (Exception ex)
                {
                    pending.Completion?.TrySetResult(false);
                    Service.Log.Error($"[FloorEvidenceJournal] Failed to append floor {pending.Bundle.FloorInstanceId}: {ex}");
                }
            }
        }
        catch (Exception ex)
        {
            Service.Log.Error($"[FloorEvidenceJournal] Writer failed for {journalPath}: {ex}");
            _pending.Writer.TryComplete(ex);
        }
    }

    private void NotifyObserver(FloorEvidenceBundle bundle)
    {
        if (_observer == null)
            return;

        try
        {
            _observer.OnFloorEvidencePersisted(bundle);
        }
        catch (Exception ex)
        {
            Service.Log.Error(
                $"[FloorEvidenceJournal] Host observer rejected floor {bundle.FloorInstanceId}: {ex}");
        }
    }

    private readonly record struct PendingWrite(
        FloorEvidenceBundle Bundle,
        TaskCompletionSource<bool>? Completion);
}

internal sealed class FloorEvidenceSession
{
    private const string RoomBindingSource = "generated-room-center-v1";
    private readonly long _startedAtMilliseconds = Environment.TickCount64;
    private readonly List<FloorEvidenceSemanticEvent> _semanticEvents = [];
    private readonly List<FloorEvidenceEffectTransition> _effectTransitions = [];
    private readonly List<ExactHoardPositionObservation> _hoards = [];
    private readonly List<ActiveTrapPositionObservation> _traps = [];
    private readonly List<FloorObjectVisibilityTransition> _visibilityTransitions = [];
    private readonly Dictionary<VisibilityObjectKey, VisibleObjectState> _visibleObjects = [];
    private readonly List<VisibilityObjectKey> _visibilityRemovalBuffer = [];
    private readonly List<FloorRoomVisit> _roomVisits = [];
    private bool? _lastIntuitionActive;
    private int _lastIntuitionStock = -1;
    private bool? _lastSightConfirmed;
    private int _lastSightStock = -1;
    private int _completedObjectEvidenceScans;
    private int _firstObjectEvidenceRelativeMilliseconds = -1;
    private int _lastObjectEvidenceRelativeMilliseconds;
    private bool _objectEvidenceUnavailable;
    private int? _authoritativeRevealRelativeMilliseconds;
    private bool _finalized;

    public FloorEvidenceSession(
        string collectorVersion,
        uint dungeonId,
        byte floor,
        uint territoryId,
        int activeLayoutIndex,
        FloorEvidenceAcquisitionMode acquisitionMode,
        FloorRoomBinding[] roomBindings)
    {
        Bundle = new FloorEvidenceBundle
        {
            CollectorVersion = collectorVersion,
            GameBuild = InstancesManager.CurrentVersion,
            FloorInstanceId = Guid.NewGuid().ToString("N"),
            DungeonId = dungeonId,
            Floor = floor,
            FloorSetStart = ((floor - 1) / 10) * 10 + 1,
            TerritoryId = territoryId,
            ActiveLayoutIndex = activeLayoutIndex,
            AcquisitionMode = acquisitionMode,
            RoomBindings = roomBindings
        };
    }

    public FloorEvidenceBundle Bundle { get; }

    public void ConfigureControlledSurvey(
        ControlledSurveyFloorRole floorRole,
        IReadOnlyList<byte> scheduledTargets)
    {
        if (_finalized)
            return;
        Bundle.ControlledSurvey.FloorRole = floorRole;
        Bundle.ControlledSurvey.ScheduledTargetFloors = scheduledTargets.ToArray();
        if (floorRole == ControlledSurveyFloorRole.SelectedTarget)
            Bundle.ControlledSurvey.Outcome = ControlledPtSurveyTargetOutcome.Pending;
    }

    public void ObserveControlledOutcome(ControlledPtSurveyTargetOutcome outcome)
    {
        if (!_finalized)
            Bundle.ControlledSurvey.Outcome = outcome;
    }

    public void ObserveControlledCaptureItem(ControlledPtSurveyItemAction item)
    {
        if (!_finalized)
            Bundle.ControlledSurvey.CaptureItem = item;
    }

    public void ObserveAuthoritativeRevealConfirmed()
    {
        if (_finalized || _authoritativeRevealRelativeMilliseconds.HasValue)
            return;

        _authoritativeRevealRelativeMilliseconds = RelativeMilliseconds();
        Bundle.ControlledSurvey.AuthoritativeRevealRelativeMilliseconds =
            _authoritativeRevealRelativeMilliseconds;
    }

    public void ObserveControlledIntuitionResolution(
        ControlledPtIntuitionResolutionSource source,
        int elapsedMilliseconds,
        int windowMilliseconds)
    {
        if (_finalized)
            return;
        Bundle.ControlledSurvey.IntuitionResolutionSource = source;
        Bundle.ControlledSurvey.IntuitionResolutionElapsedMilliseconds = Math.Max(0, elapsedMilliseconds);
        Bundle.ControlledSurvey.IntuitionResolutionWindowMilliseconds = Math.Max(0, windowMilliseconds);
    }

    public void ObserveInheritedIntuitionResolution(
        InheritedIntuitionResolutionSource source,
        int elapsedMilliseconds,
        int windowMilliseconds)
    {
        if (_finalized)
            return;
        Bundle.InheritedIntuitionResolution.Source = source;
        Bundle.InheritedIntuitionResolution.ElapsedMilliseconds = Math.Max(0, elapsedMilliseconds);
        Bundle.InheritedIntuitionResolution.ResolutionWindowMilliseconds = Math.Max(0, windowMilliseconds);
    }

    public static unsafe FloorRoomBinding[] BuildRoomBindings(
        InstanceContentDeepDungeon* dd,
        IReadOnlyList<int> reachableRooms)
    {
        var bindings = new List<FloorRoomBinding>(reachableRooms.Count);
        for (int i = 0; i < reachableRooms.Count; i++)
        {
            int roomIndex = reachableRooms[i];
            if (!MapPos.TryGetRoomCenter(dd, roomIndex, out var roomCenter))
                continue;

            bindings.Add(new FloorRoomBinding
            {
                RoomIndex = roomIndex,
                ConnectionFlags = (uint)dd->MapData[roomIndex],
                RoomCenter = ToRawPosition(roomCenter),
                BindingSource = RoomBindingSource
            });
        }

        return bindings.ToArray();
    }

    public void ObserveSemanticMessage(uint messageId, bool accepted)
    {
        if (_finalized)
            return;

        _semanticEvents.Add(new FloorEvidenceSemanticEvent
        {
            MessageId = messageId,
            RelativeMilliseconds = RelativeMilliseconds(),
            Accepted = accepted
        });
    }

    public void ObserveEffectStates(
        bool intuitionActive,
        int intuitionStock,
        bool sightConfirmed,
        int sightStock,
        string source)
    {
        if (_finalized)
            return;

        int relativeMilliseconds = RelativeMilliseconds();
        if (_lastIntuitionActive != intuitionActive || _lastIntuitionStock != intuitionStock)
        {
            _lastIntuitionActive = intuitionActive;
            _lastIntuitionStock = intuitionStock;
            _effectTransitions.Add(new FloorEvidenceEffectTransition
            {
                Effect = FloorEvidenceEffect.Intuition,
                RelativeMilliseconds = relativeMilliseconds,
                Active = intuitionActive,
                Stock = intuitionStock,
                Source = source
            });
        }

        if (_lastSightConfirmed != sightConfirmed || _lastSightStock != sightStock)
        {
            _lastSightConfirmed = sightConfirmed;
            _lastSightStock = sightStock;
            _effectTransitions.Add(new FloorEvidenceEffectTransition
            {
                Effect = FloorEvidenceEffect.Sight,
                RelativeMilliseconds = relativeMilliseconds,
                Active = sightConfirmed,
                Stock = sightStock,
                Source = source
            });
        }

        var decision = Bundle.SightResearchDecision;
        if (decision.ActionDispatched &&
            decision.RevealResource == SightResearchRevealResource.Sight &&
            (!decision.SightStockAfter.HasValue || sightStock != decision.SightStockBefore))
        {
            decision.SightStockAfter = sightStock;
        }
    }

    public void ObserveResearchDecision(
        SightResearchDecision decision,
        int resourceStock,
        SightResearchRevealResource existingRevealResource =
            SightResearchRevealResource.None)
    {
        if (_finalized)
            return;

        var observation = Bundle.SightResearchDecision;
        if (observation.ActionDispatched)
            return;

        observation.Kind = decision.Kind;
        SightResearchRevealResource revealResource =
            decision.RevealResource != SightResearchRevealResource.None
                ? decision.RevealResource
                : existingRevealResource;
        if (revealResource != SightResearchRevealResource.None)
            observation.RevealResource = revealResource;
        observation.Eligible = decision.ShouldCollectJointScan;
        observation.Reason = decision.Reason;
        observation.DecisionRelativeMilliseconds = RelativeMilliseconds();
        if (!observation.ActionAttempted)
            observation.ResourceStockBefore = resourceStock;
        if (decision.ShouldCollectJointScan &&
            Bundle.AcquisitionMode == FloorEvidenceAcquisitionMode.NaturalGameplay)
        {
            Bundle.AcquisitionMode = FloorEvidenceAcquisitionMode.AutomaticCommunitySurvey;
        }
    }

    public void ObserveResearchAction(
        bool dispatched,
        SightResearchRevealResource resource,
        int resourceStockBefore)
    {
        if (_finalized)
            return;

        var observation = Bundle.SightResearchDecision;
        observation.ActionAttempted = true;
        observation.ActionDispatched |= dispatched;
        observation.ActionRelativeMilliseconds = RelativeMilliseconds();
        observation.RevealResource = resource;
        observation.ResourceStockBefore = resourceStockBefore;
        if (resource == SightResearchRevealResource.Sight)
            observation.SightStockBefore = resourceStockBefore;
    }

    public void ObserveResearchAuthoritativeRevealConfirmed()
    {
        if (_finalized)
            return;

        var observation = Bundle.SightResearchDecision;
        if (observation.AuthoritativeRevealConfirmed)
            return;

        observation.AuthoritativeRevealConfirmed = true;
        observation.AuthoritativeRevealRelativeMilliseconds = RelativeMilliseconds();
    }

    public void ObserveResearchJointScanComplete()
    {
        if (_finalized)
            return;

        var observation = Bundle.SightResearchDecision;
        observation.AuthoritativeRevealConfirmed = true;
        observation.JointScanComplete = true;
    }

    public unsafe void ObserveObjectEvidence(
        InstanceContentDeepDungeon* dd,
        FloorObjectEvidenceSnapshot snapshot)
    {
        if (_finalized)
            return;

        int relativeMilliseconds = RelativeMilliseconds();
        if (!snapshot.Available)
        {
            _objectEvidenceUnavailable = true;
            return;
        }

        _completedObjectEvidenceScans++;
        if (_firstObjectEvidenceRelativeMilliseconds < 0)
            _firstObjectEvidenceRelativeMilliseconds = relativeMilliseconds;
        _lastObjectEvidenceRelativeMilliseconds = relativeMilliseconds;
        if (snapshot.PlayerPosition.HasValue)
            ObserveVisibilityTransitions(snapshot, snapshot.PlayerPosition.Value, relativeMilliseconds);

        for (int i = 0; i < snapshot.HoardIndicators.Count; i++)
        {
            var indicator = snapshot.HoardIndicators[i].Object;
            ObserveHoard(dd, indicator, ExactHoardObservationSource.Indicator, relativeMilliseconds);
        }

        for (int i = 0; i < snapshot.Chests.Count; i++)
        {
            var chest = snapshot.Chests[i];
            if (chest.Kind == FloorChestKind.Banded)
                ObserveHoard(dd, chest.Object, ExactHoardObservationSource.Banded, relativeMilliseconds);
        }

        for (int i = 0; i < snapshot.SightTrapIndicators.Count; i++)
            ObserveTrap(dd, snapshot.SightTrapIndicators[i], relativeMilliseconds);
    }

    public void ObserveRoomVisit(int roomIndex)
    {
        if (_finalized || roomIndex < 0)
            return;

        int relativeMilliseconds = RelativeMilliseconds();
        for (int i = 0; i < _roomVisits.Count; i++)
        {
            if (_roomVisits[i].RoomIndex != roomIndex)
                continue;

            _roomVisits[i].LastSeenRelativeMilliseconds = relativeMilliseconds;
            return;
        }

        _roomVisits.Add(new FloorRoomVisit
        {
            RoomIndex = roomIndex,
            FirstSeenRelativeMilliseconds = relativeMilliseconds,
            LastSeenRelativeMilliseconds = relativeMilliseconds
        });
    }

    public bool HasVisitedRoom(int roomIndex)
    {
        for (int i = 0; i < _roomVisits.Count; i++)
        {
            if (_roomVisits[i].RoomIndex == roomIndex)
                return true;
        }

        return false;
    }

    public FloorEvidenceBundle Finalize(string terminationReason)
    {
        if (_finalized)
            throw new InvalidOperationException($"Floor evidence session {Bundle.FloorInstanceId} was finalized twice.");

        _finalized = true;
        Bundle.SemanticEvents = _semanticEvents.ToArray();
        Bundle.EffectTransitions = _effectTransitions.ToArray();
        Bundle.ExactHoardObservations = _hoards.ToArray();
        Bundle.ActiveTrapObservations = _traps.ToArray();
        Bundle.ObjectVisibilityTransitions = _visibilityTransitions.ToArray();
        Bundle.RoomVisits = _roomVisits.ToArray();
        Bundle.Coverage = new FloorEvidenceCoverage
        {
            CompletedObjectEvidenceScans = _completedObjectEvidenceScans,
            FirstObjectEvidenceRelativeMilliseconds = Math.Max(0, _firstObjectEvidenceRelativeMilliseconds),
            LastObjectEvidenceRelativeMilliseconds = _lastObjectEvidenceRelativeMilliseconds,
            ObjectEvidenceUnavailable = _objectEvidenceUnavailable
        };
        Bundle.FinalizedRelativeMilliseconds = RelativeMilliseconds();
        Bundle.TerminationReason = terminationReason;
        return Bundle;
    }

    private unsafe void ObserveHoard(
        InstanceContentDeepDungeon* dd,
        FloorObjectEvidence evidence,
        ExactHoardObservationSource source,
        int relativeMilliseconds)
    {
        var rawPosition = ToRawPosition(evidence.Position);
        for (int i = 0; i < _hoards.Count; i++)
        {
            var existing = _hoards[i];
            if (existing.Source != source ||
                existing.GameObjectId != evidence.GameObjectId &&
                !RawWorldPosition.CanonicallyEquals(existing.RawWorldPosition, rawPosition))
            {
                continue;
            }

            existing.LastSeenRelativeMilliseconds = relativeMilliseconds;
            existing.ObservationCount++;
            return;
        }

        int roomIndex = ResolveRoom(evidence.Position);
        _hoards.Add(new ExactHoardPositionObservation
        {
            RawWorldPosition = rawPosition,
            RoomIndex = roomIndex,
            RoomCenter = TryGetRoomCenter(dd, roomIndex),
            Source = source,
            BaseId = evidence.BaseId,
            GameObjectId = evidence.GameObjectId,
            FirstSeenRelativeMilliseconds = relativeMilliseconds,
            LastSeenRelativeMilliseconds = relativeMilliseconds,
            ObservationCount = 1
        });
    }

    private unsafe void ObserveTrap(
        InstanceContentDeepDungeon* dd,
        FloorObjectEvidence evidence,
        int relativeMilliseconds)
    {
        var rawPosition = ToRawPosition(evidence.Position);
        for (int i = 0; i < _traps.Count; i++)
        {
            var existing = _traps[i];
            if (existing.GameObjectId != evidence.GameObjectId &&
                !RawWorldPosition.CanonicallyEquals(existing.RawWorldPosition, rawPosition))
            {
                continue;
            }

            existing.LastSeenRelativeMilliseconds = relativeMilliseconds;
            existing.ObservationCount++;
            return;
        }

        int roomIndex = ResolveRoom(evidence.Position);
        _traps.Add(new ActiveTrapPositionObservation
        {
            RawWorldPosition = rawPosition,
            RoomIndex = roomIndex,
            RoomCenter = TryGetRoomCenter(dd, roomIndex),
            Source = ActiveTrapObservationSource.Sight,
            BaseId = evidence.BaseId,
            GameObjectId = evidence.GameObjectId,
            FirstSeenRelativeMilliseconds = relativeMilliseconds,
            LastSeenRelativeMilliseconds = relativeMilliseconds,
            ObservationCount = 1
        });
    }

    private void ObserveVisibilityTransitions(
        FloorObjectEvidenceSnapshot snapshot,
        Vector3 playerPosition,
        int relativeMilliseconds)
    {
        _visibilityRemovalBuffer.Clear();
        foreach (var pair in _visibleObjects)
            _visibilityRemovalBuffer.Add(pair.Key);

        for (int i = 0; i < snapshot.HoardIndicators.Count; i++)
        {
            ObserveVisibleObject(
                FloorObjectVisibilityType.HoardIndicator,
                snapshot.HoardIndicators[i].Object,
                playerPosition,
                relativeMilliseconds);
        }

        for (int i = 0; i < snapshot.SightTrapIndicators.Count; i++)
        {
            ObserveVisibleObject(
                FloorObjectVisibilityType.SightTrap,
                snapshot.SightTrapIndicators[i],
                playerPosition,
                relativeMilliseconds);
        }

        for (int i = 0; i < _visibilityRemovalBuffer.Count; i++)
        {
            var key = _visibilityRemovalBuffer[i];
            if (!_visibleObjects.Remove(key, out var state))
                continue;

            _visibilityTransitions.Add(BuildVisibilityTransition(
                state.Type,
                FloorObjectVisibilityTransitionKind.Disappeared,
                state.Evidence,
                playerPosition,
                relativeMilliseconds,
                state.LastSeenRelativeMilliseconds));
        }
    }

    private void ObserveVisibleObject(
        FloorObjectVisibilityType type,
        FloorObjectEvidence evidence,
        Vector3 playerPosition,
        int relativeMilliseconds)
    {
        var key = VisibilityObjectKey.From(type, evidence);
        _visibilityRemovalBuffer.Remove(key);
        if (_visibleObjects.TryGetValue(key, out var existing))
        {
            existing.Evidence = evidence;
            existing.LastSeenRelativeMilliseconds = relativeMilliseconds;
            return;
        }

        _visibleObjects.Add(
            key,
            new VisibleObjectState(type, evidence, relativeMilliseconds));
        _visibilityTransitions.Add(BuildVisibilityTransition(
            type,
            FloorObjectVisibilityTransitionKind.Appeared,
            evidence,
            playerPosition,
            relativeMilliseconds,
            relativeMilliseconds));
    }

    private FloorObjectVisibilityTransition BuildVisibilityTransition(
        FloorObjectVisibilityType type,
        FloorObjectVisibilityTransitionKind transition,
        FloorObjectEvidence evidence,
        Vector3 playerPosition,
        int relativeMilliseconds,
        int lastSeenRelativeMilliseconds)
    {
        float dx = evidence.Position.X - playerPosition.X;
        float dy = evidence.Position.Y - playerPosition.Y;
        float dz = evidence.Position.Z - playerPosition.Z;
        return new FloorObjectVisibilityTransition
        {
            Type = type,
            Transition = transition,
            BaseId = evidence.BaseId,
            GameObjectId = evidence.GameObjectId,
            ObjectKind = evidence.ObjectKind,
            NativeCurrentDistance = evidence.NativeCurrentDistance,
            ObjectPosition = ToRawPosition(evidence.Position),
            PlayerPosition = ToRawPosition(playerPosition),
            DistanceXz = MathF.Sqrt(dx * dx + dz * dz),
            Distance3d = MathF.Sqrt(dx * dx + dy * dy + dz * dz),
            RelativeMilliseconds = relativeMilliseconds,
            LastSeenRelativeMilliseconds = lastSeenRelativeMilliseconds,
            MillisecondsSinceAuthoritativeReveal = _authoritativeRevealRelativeMilliseconds.HasValue
                ? Math.Max(0, relativeMilliseconds - _authoritativeRevealRelativeMilliseconds.Value)
                : null
        };
    }

    private int ResolveRoom(Vector3 position)
    {
        int bestRoomIndex = -1;
        float bestDistanceSquared = float.MaxValue;
        for (int i = 0; i < Bundle.RoomBindings.Length; i++)
        {
            var binding = Bundle.RoomBindings[i];
            var center = binding.RoomCenter;
            float dx = position.X - center.X;
            float dz = position.Z - center.Z;
            float distanceSquared = dx * dx + dz * dz;
            if (distanceSquared < bestDistanceSquared)
            {
                bestDistanceSquared = distanceSquared;
                bestRoomIndex = binding.RoomIndex;
            }
        }

        return bestRoomIndex;
    }

    private unsafe RawWorldPosition? TryGetRoomCenter(InstanceContentDeepDungeon* dd, int roomIndex)
    {
        return roomIndex >= 0 && MapPos.TryGetRoomCenter(dd, roomIndex, out var center)
            ? ToRawPosition(center)
            : null;
    }

    private int RelativeMilliseconds()
    {
        long elapsed = Environment.TickCount64 - _startedAtMilliseconds;
        return (int)Math.Clamp(elapsed, 0, int.MaxValue);
    }

    private static RawWorldPosition ToRawPosition(Vector3 position) =>
        new(position.X, position.Y, position.Z);

    private readonly record struct VisibilityObjectKey(
        FloorObjectVisibilityType Type,
        ulong GameObjectId,
        uint BaseId,
        int X,
        int Y,
        int Z)
    {
        public static VisibilityObjectKey From(
            FloorObjectVisibilityType type,
            in FloorObjectEvidence evidence)
        {
            const float normalization = 10f;
            return new VisibilityObjectKey(
                type,
                evidence.GameObjectId,
                evidence.BaseId,
                (int)MathF.Round(evidence.Position.X * normalization),
                (int)MathF.Round(evidence.Position.Y * normalization),
                (int)MathF.Round(evidence.Position.Z * normalization));
        }
    }

    private sealed class VisibleObjectState(
        FloorObjectVisibilityType type,
        FloorObjectEvidence evidence,
        int lastSeenRelativeMilliseconds)
    {
        public FloorObjectVisibilityType Type { get; } = type;
        public FloorObjectEvidence Evidence { get; set; } = evidence;
        public int LastSeenRelativeMilliseconds { get; set; } = lastSeenRelativeMilliseconds;
    }
}
