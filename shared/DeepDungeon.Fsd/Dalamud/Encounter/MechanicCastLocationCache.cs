using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.Game.Network;
using FFXIVClientStructs.FFXIV.Client.Network;

namespace DeepDungeon.Fsd.Dalamud.Encounter;

/// <summary>Copies the authoritative cast location before the native handler
/// discards non-ground-target locations. No packet pointers survive the detour.</summary>
internal sealed unsafe class MechanicCastLocationCache : IDisposable
{
    private const int Capacity = 512;
    private readonly record struct Observation(uint Actor, uint Action, ushort Spell, byte Type,
        Vector3 Position, float Rotation, DateTime ObservedAt, float Duration, float LastElapsed = float.NaN);
    private readonly ConcurrentQueue<Observation> _pending = new();
    private readonly Dictionary<uint, Observation> _casts = new();
    private readonly List<uint> _expired = new();
    private readonly Hook<PacketDispatcher.Delegates.HandleActorCastPacket>? _hook;
    private int _pendingCount;
    private ushort _territory;
    private DateTime _nextPrune;
    private long _faults, _reportedFaults;
    public string? Failure { get; private set; }

    public MechanicCastLocationCache()
    {
        try
        {
            var address = (nint)PacketDispatcher.MemberFunctionPointers.HandleActorCastPacket;
            if (address == nint.Zero) throw new InvalidOperationException("ActorCast handler is unavailable.");
            _hook = Service.GameInteropProvider.HookFromAddress<PacketDispatcher.Delegates.HandleActorCastPacket>(address, Receive);
            _hook.Enable();
        }
        catch (Exception ex)
        {
            _hook?.Dispose();
            _hook = null;
            throw new InvalidOperationException("Required ActorCast capture could not be initialized.", ex);
        }
    }

    private void Receive(uint entityId, ActorCastPacket* packet)
    {
        var reserved = false;
        try
        {
            if (packet == null || !float.IsFinite(packet->CastTime) || packet->CastTime < 0) return;
            if (Interlocked.Increment(ref _pendingCount) > Capacity)
            {
                Interlocked.Decrement(ref _pendingCount);
                Interlocked.Increment(ref _faults);
                return;
            }
            reserved = true;
            var p = packet->PositionInt;
            _pending.Enqueue(new(entityId, packet->ActionId, packet->SpellId, packet->ActionType,
                new(Coordinate(p[0]), Coordinate(p[1]), Coordinate(p[2])),
                packet->RotationInt * (MathF.Tau / 65535) - MathF.PI, DateTime.UtcNow, packet->CastTime));
            reserved = false;
        }
        catch
        {
            if (reserved) Interlocked.Decrement(ref _pendingCount);
            Interlocked.Increment(ref _faults);
        }
        finally { _hook!.Original(entityId, packet); }
    }

    private static float Coordinate(ushort value) => value * (2000f / 65535) - 1000;

    public void BeginFrame(ushort territory, DateTime now, bool loggedIn)
    {
        var changed = territory != _territory;
        _territory = territory;
        if (changed || !loggedIn)
        {
            _casts.Clear();
            Failure = null;
            _reportedFaults = Interlocked.Read(ref _faults);
            while (_pending.TryDequeue(out _)) Interlocked.Decrement(ref _pendingCount);
            _nextPrune = now;
            return;
        }
        while (_pending.TryDequeue(out var observation))
        {
            Interlocked.Decrement(ref _pendingCount);
            if (_casts.Count < Capacity || _casts.ContainsKey(observation.Actor))
                _casts[observation.Actor] = observation;
            else
                Interlocked.Increment(ref _faults);
        }
        var currentFaults = Interlocked.Read(ref _faults);
        if (currentFaults != _reportedFaults)
            Failure = $"ActorCast capture lost or failed to copy events (count={currentFaults}).";
        if (now < _nextPrune) return;
        _nextPrune = now.AddSeconds(1);
        var faults = Interlocked.Read(ref _faults);
        if (faults != _reportedFaults)
        {
            _reportedFaults = faults;
            Service.Log.Warning($"[Encounter] cast capture faults: {faults}");
        }
        _expired.Clear();
        foreach (var (id, observation) in _casts)
            if ((now - observation.ObservedAt).TotalSeconds > observation.Duration + 2) _expired.Add(id);
        foreach (var id in _expired) _casts.Remove(id);
    }

    public bool TryGet(uint actor, uint action, byte actionType, DateTime now, float elapsed,
        out Vector3 position, out float rotation)
    {
        position = default;
        rotation = 0;
        if (!_casts.TryGetValue(actor, out var observation) || observation.Type != actionType
            || observation.Action != action && observation.Spell != action
            || (now - observation.ObservedAt).TotalSeconds > observation.Duration + 2
            || elapsed + MasterPlayer.MechanicRuntime.MechanicCastSnapshot.ProgressResetTolerance < observation.LastElapsed)
            return false;
        _casts[actor] = observation with { LastElapsed = elapsed };
        position = observation.Position;
        rotation = observation.Rotation;
        return true;
    }

    public void RememberFallback(uint actor, uint action, byte type, DateTime now, float elapsed,
        float duration, Vector3 position, float rotation)
    {
        if (_casts.Count < Capacity || _casts.ContainsKey(actor))
            _casts[actor] = new(actor, action, 0, type, position, rotation, now.AddSeconds(-elapsed), duration, elapsed);
    }

    public void Forget(uint actor)
    {
        // A just-received packet may precede the native casting flag; don't
        // discard it until it has actually matched an observed cast.
        if (_casts.TryGetValue(actor, out var observation) && float.IsFinite(observation.LastElapsed))
            _casts.Remove(actor);
    }

    public void Dispose()
    {
        _hook?.Dispose();
        while (_pending.TryDequeue(out _)) Interlocked.Decrement(ref _pendingCount);
        _casts.Clear();
    }
}
