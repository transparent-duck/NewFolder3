using System;
using System.Numerics;
using MasterPlayer.MechanicRuntime;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Object;

namespace DeepDungeon.Fsd.Dalamud.Encounter;

// Action-effect event feed: zone/boss module cast mechanics are event-driven
// (ws.Actors.CastEvent / OpCastEvent), which only the feed's event queue
// produces. Frames alone never fire it, leaving ForbiddenZones permanently
// empty. Maps the native Receive detour onto MechanicEventActionEffect.
public sealed partial class EncounterMechanicHost
{
    private NativeEncounterEvents? _events;
    private readonly System.Collections.Generic.Dictionary<(uint Caster, uint Sequence), (DateTime At, Vector2? Feet)> _effectReceipts = new();
    private CombatActionEvidence? _combatActionEvidence;

    internal CombatActionEvidence? CombatActionEvidence => Volatile.Read(ref _combatActionEvidence);
    internal bool CapturesActionCalls => _execution?.CapturesActionCalls == true;
    internal bool CapturesActionEffects => _events?.CapturesActionEffects == true;
    internal void AttachCombatActionEvidence(CombatActionEvidence evidence) =>
        Volatile.Write(ref _combatActionEvidence, evidence);
    internal void DetachCombatActionEvidence(CombatActionEvidence evidence) =>
        Interlocked.CompareExchange(ref _combatActionEvidence, null, evidence);

    internal void ReportInputFailure(string stage, Exception error)
    {
        _eventFailure = $"{stage} failed: {error.Message}";
        Service.Log.Error(error, $"[Encounter] {_eventFailure}");
    }

    private void Detach()
    {
        _events?.Dispose();
        _events = null;
        _effectReceipts.Clear();
    }

    internal unsafe void OnRawActionEffectReceiving(uint actorId, Character* casterPtr, Vector3* targetPos,
        ActionEffectHandler.Header* header, ActionEffectHandler.TargetEffects* effects, GameObjectId* targetEntityIds)
    {
        if (_feed == null || header == null) return;
        var now = DateTime.UtcNow;
        Vector2? feet = Service.LocalPlayer is { } player ? new(player.Position.X, player.Position.Z) : null;
        if (_effectReceipts.Count >= 128) _effectReceipts.Clear();
        _effectReceipts[(actorId, header->GlobalSequence)] = (now, feet);
    }
    internal unsafe void OnRawActionEffect(uint actorId, Character* casterPtr, Vector3* targetPos,
        ActionEffectHandler.Header* header, ActionEffectHandler.TargetEffects* effects,
        GameObjectId* targetEntityIds)
    {
        if (_feed == null || header == null)
            return;
        try
        {
            var hasReceipt = _effectReceipts.Remove((actorId, header->GlobalSequence), out var receipt);
            var numTargets = header->NumTargets;
            var targets = Array.Empty<MechanicActionEffectTarget>();
            if (numTargets > 0 && targetEntityIds != null && effects != null)
            {
                targets = new MechanicActionEffectTarget[numTargets];
                for (var i = 0; i < numTargets; ++i)
                {
                    var raw = new ulong[8];
                    var perTarget = (ulong*)&effects[i];
                    for (var j = 0; j < 8; ++j)
                        raw[j] = perTarget[j];
                    targets[i] = new(targetEntityIds[i].Id, raw);
                }
            }
            var mainTarget = header->AnimationTargetId.Id != 0 ? (ulong)header->AnimationTargetId.Id
                : numTargets > 0 && targetEntityIds != null ? targetEntityIds[0].Id : 0UL;
            var firstSeen = _seenActionEffects.Count < MaxLoggedActionEffects && !_seenActionEffects.Contains((actorId, header->ActionId));
            var localId = Service.LocalPlayer?.EntityId ?? 0;
            if (localId != 0 && actorId == localId && CombatActionEvidence is { } evidence)
                evidence.Observe(new CombatActionEvidence.EffectReceipt(
                    hasReceipt ? receipt.At : DateTime.UtcNow, actorId, header->ActionType, header->ActionId,
                    mainTarget, header->SourceSequence, header->GlobalSequence, targets));
            var localKnockback = false;
            foreach (var target in targets)
                if (target.Id == localId)
                    foreach (var raw in target.RawEffects)
                        if ((byte)raw == 31) { localKnockback = true; break; }
            var timeline = firstSeen || localKnockback ? MechanicTimelineReader.Capture(casterPtr, actorId,
                header->ActionId, header->GlobalSequence, DateTime.UtcNow) : null;
            _feed.Enqueue(new MechanicEventActionEffect(
                SanitizeId(actorId), header->ActionType, header->ActionId,
                SanitizeId(mainTarget), header->AnimationLock,
                targetPos != null ? *targetPos : Vector3.Zero,
                header->GlobalSequence, header->SourceSequence, header->RotationInt, targets)
            { ReceivedAt = hasReceipt ? receipt.At : default, LocalPosition = receipt.Feet, Timeline = timeline });
            if (timeline is { } clock && firstSeen)
                Service.Log.Debug($"[Encounter] timeline-receipt action={header->ActionId} seq={header->GlobalSequence} received={receipt.At:O} observed={clock.ObservedAt:O} timeline={clock.TimelineId} tick={clock.Timestamp:R} speed={clock.Speed:R}");
            LogFirstSeenActionEffect(actorId, header->ActionType, header->ActionId);
        }
        catch (Exception ex)
        {
            _eventFailure = $"ActionEffect ingestion failed: {ex.Message}";
            Service.Log.Warning($"[Encounter] action effect event failed: {ex.Message}");
        }
    }

    // Director events have world scope; the remaining categories require a source actor.
    internal void OnActorControl(NativeEncounterEvents.ActorControlObservation observation)
    {
        try { ApplyActorControl(observation); }
        catch (Exception ex)
        {
            _eventFailure = $"ActorControl ingestion failed: {ex.Message}";
            Service.Log.Error(ex, "[Encounter] actor control ingestion failed");
        }
    }

    private void ApplyActorControl(NativeEncounterEvents.ActorControlObservation observation)
    {
        var feed = _feed;
        var source = SanitizeId(observation.ActorId);
        var category = (BossMod.Network.ServerIPC.ActorControlCategory)observation.Category;
        if (feed != null && category == BossMod.Network.ServerIPC.ActorControlCategory.DirectorUpdate)
        {
            feed.Enqueue(new MechanicEventDirectorUpdate(observation.Param1, observation.Param2,
                observation.Param3, observation.Param4, observation.Param5, observation.Param6));
            return;
        }
        if (feed == null || source == 0)
            return;
        switch (category)
        {
            case BossMod.Network.ServerIPC.ActorControlCategory.EObjAnimation:
                feed.Enqueue(new MechanicEventEObjAnimation(source, (ushort)observation.Param1, (ushort)observation.Param2));
                break;
            case BossMod.Network.ServerIPC.ActorControlCategory.EObjSetState:
                feed.Enqueue(new MechanicEventEObjSetState(source, (ushort)observation.Param1));
                break;
            case BossMod.Network.ServerIPC.ActorControlCategory.TargetIcon:
                feed.Enqueue(new MechanicEventIcon(observation.Param1,
                    SanitizeId(observation.Param2), source));
                break;
            case BossMod.Network.ServerIPC.ActorControlCategory.PlayActionTimeline:
                feed.Enqueue(new MechanicEventPlayActionTimeline(source,
                    (ushort)observation.Param1));
                break;
        }
    }

    private static ulong SanitizeId(ulong id) =>
        id is 0 or 0xE0000000 || (id >> 56) == 0xFF ? 0 : id;

    // Diagnostics: log each new (caster, actionId) pair once so we can tell
    // whether hostile casts reach the feed when zone counts stay zero.
    private readonly System.Collections.Generic.HashSet<(ulong, uint)> _seenActionEffects = new();
    private const int MaxLoggedActionEffects = 64;

    private void LogFirstSeenActionEffect(uint casterId, byte actionType, uint actionId)
    {
        if (_seenActionEffects.Count >= MaxLoggedActionEffects)
            return;
        var local = Service.LocalPlayer;
        if (local != null && casterId == (uint)local.GameObjectId)
            return;
        if (_seenActionEffects.Add((casterId, actionId)))
            Service.Log.Debug($"[Encounter] action-effect: caster=0x{casterId:X8} type={actionType} aid={actionId}");
    }
}
