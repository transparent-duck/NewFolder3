using System;
using System.Collections.Concurrent;
using System.Threading;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.Game.Network;
using MasterPlayer.MechanicRuntime;
using OmenTools.Interop.Game.Models;

namespace DeepDungeon.Fsd.Dalamud.Encounter;

/// <summary>Copies director map events; the framework thread owns the world feed.</summary>
internal sealed unsafe class MechanicMapEffectCapture : IDisposable
{
    private delegate void ProcessMapEffect(void* framework, MapEffectPacket* packet);
    private readonly Hook<ProcessMapEffect> _hook;
    private readonly ConcurrentQueue<(byte Index, uint State)> _pending = new();
    private int _count;
    private ushort _territory;
    private long _dropped, _reportedDropped;
    public string? Failure { get; private set; }

    public MechanicMapEffectCapture()
    {
        // Installed CN executable: unique executable match at RVA B82D60.
        // RCX=framework, RDX=packet; reads index+8, low+4 and high+6.
        // FFCS HandleMapEffectPacket aliases the replay dispatcher on this SDK.
        // Same verified native contract as Blunderville PacketObservations.
        _hook = new CompSig("48 89 5C 24 ?? 57 48 83 EC ?? 48 8B F9 48 8B DA 48 8B 89 58 01 00 00 48 85 C9 74 ?? 48 8B 01 FF 50 18 84 C0")
            .GetHook<ProcessMapEffect>(Receive);
        try { _hook.Enable(); }
        catch { _hook.Dispose(); throw; }
    }

    private void Receive(void* framework, MapEffectPacket* packet)
    {
        try
        {
            if (packet == null) return;
            if (Interlocked.Increment(ref _count) > 512)
            {
                Interlocked.Decrement(ref _count);
                Interlocked.Increment(ref _dropped);
                return;
            }
            _pending.Enqueue((packet->Index, packet->State | ((uint)packet->TimelineIndex << 16)));
        }
        finally { _hook.Original(framework, packet); }
    }

    public void Drain(MechanicWorldFeed feed, ushort territory, bool loggedIn)
    {
        var discard = !loggedIn || territory != _territory;
        if (discard)
        {
            Failure = null;
            _reportedDropped = Interlocked.Read(ref _dropped);
        }
        _territory = territory;
        while (_pending.TryDequeue(out var next))
        {
            Interlocked.Decrement(ref _count);
            if (!discard) feed.ApplyEvent(new MechanicEventMapEffect(next.Index, next.State));
        }
        var dropped = Interlocked.Read(ref _dropped);
        if (dropped != _reportedDropped)
        {
            _reportedDropped = dropped;
            Failure = $"MapEffect capture lost events (count={dropped}).";
            Service.Log.Warning($"[Encounter] map capture dropped events: {dropped}");
        }
    }

    public void Dispose()
    {
        _hook.Dispose();
        while (_pending.TryDequeue(out _)) Interlocked.Decrement(ref _count);
    }
}
