using System.Numerics;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace DeepDungeon.Fsd.Dalamud.Encounter;

/// <summary>Applies mechanic action restrictions and observes calls for the active FSD floor.</summary>
internal sealed unsafe class MechanicActionGate : IDisposable
{
    private readonly EncounterMechanicHost _host;
    private Hook<ActionManager.Delegates.UseAction>? _use;
    private Hook<ActionManager.Delegates.UseActionLocation>? _location;
    internal bool ActionsBlocked { get; set; }
    internal bool CapturesActionCalls => _use?.IsEnabled == true && _location?.IsEnabled == true;

    internal MechanicActionGate(EncounterMechanicHost host)
    {
        _host = host;
        try
        {
            _use = Service.GameInteropProvider.HookFromAddress<ActionManager.Delegates.UseAction>(
                (nint)ActionManager.MemberFunctionPointers.UseAction, Use);
            _location = Service.GameInteropProvider.HookFromAddress<ActionManager.Delegates.UseActionLocation>(
                (nint)ActionManager.MemberFunctionPointers.UseActionLocation, UseLocation);
            _use.Enable(); _location.Enable();
        }
        catch { Dispose(); throw; }
    }

    private bool Use(ActionManager* self, ActionType type, uint id, ulong target, uint extra,
        ActionManager.UseActionMode mode, uint route, bool* area)
    {
        var evidence = _host.CombatActionEvidence;
        bool blocked = ActionsBlocked;
        if (evidence == null)
            return !blocked && _use!.Original(self, type, id, target, extra, mode, route, area);

        ushort? before = self == null ? null : self->LastUsedActionSequence;
        var hardTarget = CurrentHardTarget();
        bool result = !blocked && _use!.Original(self, type, id, target, extra, mode, route, area);
        Observe(evidence, self, before, false, type, id, target, hardTarget, mode, blocked, result);
        return result;
    }

    private bool UseLocation(ActionManager* self, ActionType type, uint id, ulong target,
        Vector3* location, uint extra, byte a7)
    {
        var evidence = _host.CombatActionEvidence;
        bool blocked = ActionsBlocked;
        if (evidence == null)
            return !blocked && _location!.Original(self, type, id, target, location, extra, a7);

        ushort? before = self == null ? null : self->LastUsedActionSequence;
        var hardTarget = CurrentHardTarget();
        bool result = !blocked && _location!.Original(self, type, id, target, location, extra, a7);
        Observe(evidence, self, before, true, type, id, target, hardTarget, null, blocked, result);
        return result;
    }

    private static ulong? CurrentHardTarget() => Service.Framework.IsInFrameworkUpdateThread
        ? Service.TargetManager.Target?.GameObjectId : null;

    private static void Observe(CombatActionEvidence evidence, ActionManager* self, ushort? before,
        bool groundTargeted, ActionType type, uint id, ulong target, ulong? hardTarget,
        ActionManager.UseActionMode? mode, bool blocked, bool result)
    {
        ushort? sequence = self != null && before.HasValue && self->LastUsedActionSequence != before.Value
            ? self->LastUsedActionSequence : null;
        bool queued = self != null && self->ActionQueued;
        evidence.Observe(new CombatActionEvidence.ActionCall(
            DateTime.UtcNow, groundTargeted, type, id, target, hardTarget, mode, blocked, result,
            sequence, queued ? self->QueuedActionId : null, queued ? self->QueuedTargetId.Id : null));
    }

    public void Dispose()
    {
        ActionsBlocked = false;
        _location?.Dispose(); _location = null;
        _use?.Dispose(); _use = null;
    }
}
