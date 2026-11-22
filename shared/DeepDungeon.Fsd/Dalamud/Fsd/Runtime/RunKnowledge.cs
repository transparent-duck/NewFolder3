using System;
using System.Collections.Generic;
using System.Numerics;
using DeepDungeon.Fsd.Core;

namespace DeepDungeon.Fsd.Dalamud.Runtime;

/// <summary>Work identity bound to one committed site: floor generation +
/// stable site key + attempt ordinal. A typed outcome always returns to the
/// captured token, never to whatever head happens to be current.</summary>
internal readonly record struct SiteWorkToken(long FloorGeneration, string Key, long Attempt);

internal enum SiteWorkOutcome { Completed, PolicySkipped, Failed, Cancelled }

/// <summary>Why room intel was required. A purpose is sticky: once completed
/// for a room it stays completed for the rest of the floor; head-list churn
/// and new positions can never un-complete it.</summary>
internal enum RoomIntelPurpose { Discovery, IntuitionPosition }

/// <summary>Exact cost/probability helpers shared by the scheduler.</summary>
internal static class HoardRateMath
{
    /// <summary>Remaining hoard mass at one unchecked candidate. Completed
    /// probes remove p·q of the floor's hoard mass; the remainder stays
    /// concentrated (not renormalized) so no-hoard mass is preserved.</summary>
    internal static double RemainingPointProbability(double pFloor, double q, double eliminated)
    {
        var remaining = 1d - eliminated;
        return remaining > 0d ? pFloor * q / remaining : 0d;
    }

    /// <summary>Observed collection rate (treasure per second). Positive only
    /// after an actual native-count delta exists; no prior is invented.</summary>
    internal static double? ObservedHoardRate(int collected, double elapsedSeconds) =>
        collected > 0 && elapsedSeconds > 0 ? collected / elapsedSeconds : null;
}

/// <summary>
/// Floor-scoped facts owned by the run's knowledge: what this floor proved.
/// Pure state and derived facts only — no nav, planner, op, item or
/// cancellation calls live here; callers ingest and read.
/// </summary>
internal sealed class FloorKnowledge
{
    internal FloorKnowledge(long generation, uint dungeonId, byte floor, int spawnRoom, int hoardBaseline)
    {
        Generation = generation;
        DungeonId = dungeonId;
        Floor = floor;
        SpawnRoom = spawnRoom;
        HoardBaseline = hoardBaseline;
    }

    internal long Generation { get; }
    internal uint DungeonId { get; }
    internal byte Floor { get; }
    internal int SpawnRoom { get; private set; }
    internal int HoardBaseline { get; private set; }

    /// <summary>The controller binds floor knowledge at construction before
    /// the native frame is read; the first ingest seals the real spawn room
    /// and hoard baseline.</summary>
    internal void SealFloor(int spawnRoom, int hoardBaseline)
    {
        if (SpawnRoom >= 0)
            return;
        SpawnRoom = spawnRoom;
        HoardBaseline = hoardBaseline;
        LatestHoardCount = hoardBaseline;
        Bump();
    }

    /// <summary>Bumped on every mutation; the scheduler rebuilds its route
    /// only when this (or its other inputs) changed.</summary>
    internal long Revision { get; private set; }

    // ---- chat/native item facts (ChatWatchers/FloorItemExecutor stay the
    // input adapters; these are their derived truth for this floor) ----
    /// <summary>Confirmed Intuition use this floor; pending dispatch stays
    /// a separate fact — HasCurrentFloorIntuitionUse is either.</summary>
    internal bool UsedIntuitionThisFloor { get; private set; }
    internal bool IntuitionUsePendingThisFloor { get; private set; }
    internal bool HasCurrentFloorIntuitionUse =>
        UsedIntuitionThisFloor || IntuitionUsePendingThisFloor;
    internal bool ChatSaysHoard { get; private set; }
    internal bool ChatSaysNoHoard { get; private set; }
    private bool _inheritedWindowElapsed;
    internal bool InheritedNoHoardInferred =>
        _inheritedWindowElapsed && !ChatSaysHoard && !ExactHoardPosition.HasValue &&
        ObservedBandedPositions.Count == 0;
    internal bool NativeIntuitionActive { get; private set; }

    // ---- hoard facts ----
    internal bool HoardAcquired { get; private set; }
    internal int LatestHoardCount { get; private set; }
    /// <summary>Exact reveal position from the actual hoard-indicator
    /// object. Survives object unload — it is a position, not a live
    /// entity.</summary>
    internal Vector3? ExactHoardPosition { get; private set; }
    internal int? ExactHoardRoomIndex { get; private set; }
    internal readonly List<Vector3> ObservedBandedPositions = new();
    internal readonly List<RawWorldPosition> ObservedSightPositions = new();

    // ---- completion truth (sticky within the floor) ----
    private readonly HashSet<string> _completedProbes = new(StringComparer.Ordinal);
    private readonly HashSet<ulong> _completedChestEntities = new();
    private readonly HashSet<(int Room, RoomIntelPurpose Purpose)> _completedIntel = new();
    private readonly HashSet<uint> _confirmedPomanderUses = new();
    private readonly HashSet<uint> _confirmedStoneUses = new();

    /// <summary>Canonical probe-key registry per room: the same physical
    /// point (model/palantir/sight) reuses one key — identity is canonical
    /// position, not a 3-decimal formatting accident.</summary>
    private readonly Dictionary<int, List<(RawWorldPosition Position, string Key)>> _probeIdentities = new();

    internal string GetProbeKey(int room, in RawWorldPosition position)
    {
        if (!_probeIdentities.TryGetValue(room, out var entries))
            _probeIdentities[room] = entries = new();
        foreach (var entry in entries)
            if (RawWorldPosition.CanonicallyEquals(entry.Position, position))
                return entry.Key;
        var key = FormattableString.Invariant(
            $"cand:{room}:{position.X:R}:{position.Y:R}:{position.Z:R}");
        entries.Add((position, key));
        return key;
    }

    /// <summary>Observed chest entities (entity/kind/position — never a
    /// live pointer): a revealed chest survives object unload so the route
    /// can still reach it and live evidence confirms the open.</summary>
    internal readonly record struct ObservedChest(
        ulong EntityId, int Kind, Vector3 Position, int RoomIndex);
    internal readonly List<ObservedChest> ObservedChests = new();

    internal void ObserveChest(ulong entityId, int kind, Vector3 position, int roomIndex)
    {
        foreach (var c in ObservedChests)
            if (c.EntityId == entityId)
                return;
        ObservedChests.Add(new ObservedChest(entityId, kind, position, roomIndex));
        Bump();
    }

    internal int CompletedProbeCount => _completedProbes.Count;

    /// <summary>Actual sight use-state — attempted vs confirmed are
    /// distinct facts; "used" means confirmed, pending stays visible.</summary>
    internal SightUseState SightState { get; private set; }
    internal bool UsedSightThisFloor => SightState == SightUseState.Confirmed;
    internal bool SightUsePendingThisFloor => SightState == SightUseState.Attempted;

    internal void ObserveSightState(SightUseState state)
    {
        if (SightState == state)
            return;
        SightState = state;
        Bump();
    }

    internal void RecordConfirmedPomanderUse(uint itemId)
    {
        if (_confirmedPomanderUses.Add(itemId))
            Bump();
    }

    internal void RecordConfirmedStoneUse(uint itemId)
    {
        if (_confirmedStoneUses.Add(itemId))
            Bump();
    }

    internal bool WasPomanderUseConfirmed(uint itemId) => _confirmedPomanderUses.Contains(itemId);
    internal bool WasStoneUseConfirmed(uint itemId) => _confirmedStoneUses.Contains(itemId);

    private void Bump() => Revision++;

    internal void ObserveIntuitionUse(bool confirmed, bool awaitingResult)
    {
        if (UsedIntuitionThisFloor == confirmed && IntuitionUsePendingThisFloor == awaitingResult)
            return;
        UsedIntuitionThisFloor = confirmed;
        IntuitionUsePendingThisFloor = awaitingResult;
        Bump();
    }

    internal bool ObserveIntuitionResult(bool hasHoard)
    {
        // Positive evidence is irrevocable for this floor until acquisition.
        if (ChatSaysHoard || (!hasHoard && ChatSaysNoHoard))
            return false;
        ChatSaysHoard = hasHoard;
        ChatSaysNoHoard = !hasHoard;
        Bump();
        return true;
    }

    internal void ObserveNativeIntuition(bool active)
    {
        if (NativeIntuitionActive == active)
            return;
        NativeIntuitionActive = active;
        Bump();
    }

    internal void MarkInheritedNoHoard()
    {
        if (_inheritedWindowElapsed)
            return;
        _inheritedWindowElapsed = true;
        Bump();
    }

    /// <summary>Native hoard counter ingest: acquisition is the delta over
    /// the floor baseline via the existing lifecycle planner, never a
    /// chat/site-completion guess.</summary>
    internal bool ObserveHoardCount(int count)
    {
        var decision = HoardIndicatorLifecyclePlanner.Decide(new HoardIndicatorLifecycleSnapshot(
            HoardAcquired, HoardBaseline, count));
        var changed = LatestHoardCount != count || (decision.MarkHoardOpened && !HoardAcquired);
        LatestHoardCount = count;
        if (decision.MarkHoardOpened)
        {
            HoardAcquired = true;
            // Acquired retires every hoard-discovery fact at once.
            ExactHoardPosition = null;
            ExactHoardRoomIndex = null;
        }
        if (changed)
            Bump();
        return changed;
    }

    internal void SetExactHoard(Vector3 position, int? roomIndex)
    {
        if (ExactHoardPosition.HasValue &&
            Vector3.DistanceSquared(ExactHoardPosition.Value, position) <= 0.01f &&
            ExactHoardRoomIndex == roomIndex)
            return;
        ExactHoardPosition = position;
        ExactHoardRoomIndex = roomIndex;
        Bump();
    }

    internal void ObserveBanded(Vector3 position)
    {
        foreach (var p in ObservedBandedPositions)
            if (Vector3.DistanceSquared(p, position) <= 0.01f)
                return;
        ObservedBandedPositions.Add(position);
        Bump();
    }

    internal void ObserveSightPosition(in RawWorldPosition position)
    {
        foreach (var p in ObservedSightPositions)
            if (RawWorldPosition.CanonicallyEquals(p, position))
                return;
        ObservedSightPositions.Add(position);
        Bump();
    }

    /// <summary>Canonical work key for a candidate point: floor-scoped room +
    /// canonical position, never a source index.</summary>
    internal static string ProbeKey(int room, in RawWorldPosition position) =>
        $"cand:{room}:{position.X:0.###}:{position.Y:0.###}:{position.Z:0.###}";

    internal void MarkProbeCompleted(string key)
    {
        if (_completedProbes.Add(key))
            Bump();
    }

    internal bool IsProbeCompleted(string key) => _completedProbes.Contains(key);

    internal void MarkChestCompleted(ulong entityId)
    {
        if (entityId != 0 && _completedChestEntities.Add(entityId))
            Bump();
    }

    internal bool IsChestCompleted(ulong entityId) =>
        entityId != 0 && _completedChestEntities.Contains(entityId);

    internal void MarkIntelCompleted(int room, RoomIntelPurpose purpose)
    {
        if (_completedIntel.Add((room, purpose)))
            Bump();
    }

    internal bool IsIntelCompleted(int room, RoomIntelPurpose purpose) =>
        _completedIntel.Contains((room, purpose));

    // ---- scheduler-visible derived state (written by the work scheduler,
    // read by compatibility projections and telemetry) ----
    internal HoardEvidenceState EvidenceState { get; set; } = HoardEvidenceState.Disabled;
    internal int RequiredWorkRemaining { get; set; }
    internal int PendingWorkCount { get; set; }
    internal bool PendingIntelWork { get; set; }
    internal bool SearchObligationsResolved { get; set; }
    /// <summary>Set after the scheduler's first real work decision — callers
    /// must not read default-zero counters as "floor resolved".</summary>
    internal bool WorkDecisionInitialized { get; set; }
    internal bool PendingBandedWork { get; set; }
    internal string SelectionSource { get; set; } = "none";
    internal double? ActiveHoardRate { get; set; }
}

/// <summary>
/// Run-scoped knowledge: owns the current floor knowledge, the run's
/// pomander inventory/native-effect snapshot, and the observed collection
/// rate baseline. Run state survives floor transitions where the native
/// game state does (item effects, counters); floor facts do not.
/// </summary>
internal sealed class RunKnowledge
{
    internal const int PomanderSlotCount = 16;

    internal FloorKnowledge? Floor { get; private set; }

    // ---- pomander inventory / native effect snapshot ----
    private readonly int[] _pomanderStock = new int[PomanderSlotCount];
    private readonly bool[] _pomanderActive = new bool[PomanderSlotCount];
    private long _inventoryRevision;

    // ---- run collection-rate counters ----
    private long _firstReadyTickMs = -1;
    private int _startingHoardCount = -1;
    private int _latestHoardCount = -1;

    internal long InventoryRevision => _inventoryRevision;
    internal int GetPomanderStock(uint slot) =>
        slot < PomanderSlotCount ? _pomanderStock[slot] : 0;
    internal bool IsPomanderEffectActive(uint slot) =>
        slot < PomanderSlotCount && _pomanderActive[slot];
    /// <summary>A resource is available when stock &gt; 0 OR its native
    /// effect is already active.</summary>
    internal bool PomanderAvailable(uint slot) =>
        GetPomanderStock(slot) > 0 || IsPomanderEffectActive(slot);

    /// <summary>Ingest the current 16-slot inventory/effect snapshot. Called
    /// once per outer update by the floor controller — knowledge itself
    /// never reaches into game state.</summary>
    internal void SetPomanderSnapshot(IReadOnlyList<int> stock, IReadOnlyList<bool> active)
    {
        var changed = false;
        for (var i = 0; i < PomanderSlotCount; i++)
        {
            var s = i < stock.Count ? stock[i] : 0;
            var a = i < active.Count && active[i];
            if (_pomanderStock[i] != s || _pomanderActive[i] != a)
            {
                _pomanderStock[i] = s;
                _pomanderActive[i] = a;
                changed = true;
            }
        }
        if (changed)
            _inventoryRevision++;
    }

    /// <summary>Bind a new stable floor. Native effects persist across the
    /// transition (they live in the inventory snapshot, not here); the
    /// previous floor's positions/probes/chat facts do not.</summary>
    internal FloorKnowledge BeginFloor(long generation, uint dungeonId, byte floor, int spawnRoom, int hoardBaseline)
    {
        Floor = new FloorKnowledge(generation, dungeonId, floor, spawnRoom, hoardBaseline);
        if (_firstReadyTickMs < 0 && hoardBaseline >= 0)
        {
            // First stable duty floor: baseline for the observed rate.
            _firstReadyTickMs = Environment.TickCount64;
            _startingHoardCount = hoardBaseline;
            _latestHoardCount = hoardBaseline;
        }
        return Floor;
    }

    /// <summary>Latest native hoard count: feeds both the floor acquisition
    /// delta and the run's observed collection rate.</summary>
    internal bool ObserveHoardCount(int count)
    {
        if (_startingHoardCount < 0)
        {
            // Bound before the first native counter was read — capture the
            // baseline at the first count observation instead.
            _firstReadyTickMs = Environment.TickCount64;
            _startingHoardCount = count;
        }
        _latestHoardCount = count;
        return Floor?.ObserveHoardCount(count) == true;
    }

    /// <summary>Observed treasure-per-second over the whole duty, or null
    /// until a real collection delta exists (cold start: fixed fallback).</summary>
    internal double? ObservedHoardRate()
    {
        if (_firstReadyTickMs < 0 || _startingHoardCount < 0)
            return null;
        var collected = _latestHoardCount - _startingHoardCount;
        var elapsed = (Environment.TickCount64 - _firstReadyTickMs) / 1000d;
        return HoardRateMath.ObservedHoardRate(collected, elapsed);
    }

    /// <summary>Floor end drops floor-local knowledge only.</summary>
    internal void EndFloor(long generation)
    {
        if (Floor?.Generation == generation)
            Floor = null;
    }

    /// <summary>Run end clears everything.</summary>
    internal void Reset()
    {
        Floor = null;
        Array.Clear(_pomanderStock);
        Array.Clear(_pomanderActive);
        _inventoryRevision++;
        _firstReadyTickMs = -1;
        _startingHoardCount = -1;
        _latestHoardCount = -1;
    }
}
