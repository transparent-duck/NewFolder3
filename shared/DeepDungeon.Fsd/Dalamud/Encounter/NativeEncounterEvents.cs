using System.Numerics;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Object;

namespace DeepDungeon.Fsd.Dalamud.Encounter;

/// <summary>FSD event transport only. No rotation, PvP, action changes or lock removal.</summary>
internal sealed unsafe class NativeEncounterEvents : IDisposable
{
    internal readonly record struct ActorControlObservation(uint ActorId, uint Category,
        uint Param1, uint Param2, uint Param3, uint Param4, uint Param5, uint Param6);
    private delegate void ActorControl(uint actor, uint category, uint p1, uint p2, uint p3,
        uint p4, uint p5, uint p6, uint p7, uint p8, ulong target, byte replaying);
    private Hook<ActionEffectHandler.Delegates.Receive>? _effects;
    private Hook<ActorControl>? _control;
    private readonly EncounterMechanicHost _host;

    internal NativeEncounterEvents(EncounterMechanicHost host)
    {
        _host = host;
        try
        {
            _effects = Service.GameInteropProvider.HookFromAddress<ActionEffectHandler.Delegates.Receive>(
                (nint)ActionEffectHandler.MemberFunctionPointers.Receive, Receive);
            // Current SDK does not expose ProcessPacketActorControl. This is
            // the same signature and ABI used by the source FSD event transport.
            _control = Service.GameInteropProvider.HookFromSignature<ActorControl>(
                "E8 ?? ?? ?? ?? 0F B7 0B 83 E9 64", ReceiveControl);
            _effects.Enable();
            _control.Enable();
        }
        catch { Dispose(); throw; }
    }

    internal bool CapturesActionEffects => _effects?.IsEnabled == true;
    internal bool IsAvailable => CapturesActionEffects && _control?.IsEnabled == true;

    private void Receive(uint actor, Character* caster, Vector3* position, ActionEffectHandler.Header* header,
        ActionEffectHandler.TargetEffects* effects, GameObjectId* targets)
    {
        try { _host.OnRawActionEffectReceiving(actor, caster, position, header, effects, targets); }
        catch (Exception ex) { _host.ReportInputFailure("pre-receive observation", ex); }
        _effects!.Original(actor, caster, position, header, effects, targets);
        _host.OnRawActionEffect(actor, caster, position, header, effects, targets);
    }

    private void ReceiveControl(uint actor, uint category, uint p1, uint p2, uint p3,
        uint p4, uint p5, uint p6, uint p7, uint p8, ulong target, byte replaying)
    {
        try { _host.OnActorControl(new(actor, category, p1, p2, p3, p4, p5, p6)); }
        catch (Exception ex) { _host.ReportInputFailure("actor-control observation", ex); }
        finally { _control!.Original(actor, category, p1, p2, p3, p4, p5, p6, p7, p8, target, replaying); }
    }

    public void Dispose()
    {
        _control?.Dispose(); _control = null;
        _effects?.Dispose(); _effects = null;
    }
}
