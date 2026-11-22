using System;
using DeepDungeon.Fsd.Dalamud.Utilities;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using MasterPlayer.MechanicRuntime;

namespace DeepDungeon.Fsd.Dalamud.Encounter;

/// <summary>Applies mechanic restrictions once for every action producer, including item use.</summary>
internal sealed class MechanicExecutionController(EncounterMechanicHost host) : IDisposable
{
    private readonly DirectMovementController _movement = new("mechanic restriction");
    private readonly MechanicActionGate _actions = new(host);
    internal bool CapturesActionCalls => _actions.CapturesActionCalls;
    private DateTime _nextCancel;
    private ulong _restoreAutosTarget;

    public unsafe void Update(ExecutionRestrictions restrictions, DateTime now)
    {
        var previous = MechanicExecutionState.Restrictions;
        MechanicExecutionState.Publish(restrictions);
        _actions.ActionsBlocked = MechanicExecutionState.ActionsBlocked;
        if (previous != restrictions)
            Service.Log.Info($"[Encounter] execution restrictions: {previous} -> {restrictions}");

        var movementBlocked = (restrictions & ExecutionRestrictions.Movement) != 0;
        if (_movement.SuppressInput != movementBlocked) _movement.SuppressInput = movementBlocked;
        var manager = ActionManager.Instance();
        var ui = UIState.Instance();
        if ((restrictions & ExecutionRestrictions.Actions) != 0)
        {
            // A queued action must not survive the stop and execute on release.
            if (manager != null) manager->ActionQueued = false;
            if (ui != null && ui->WeaponState.AutoAttackState.Get())
            {
                if ((previous & ExecutionRestrictions.Actions) == 0)
                    _restoreAutosTarget = Service.TargetManager.Target?.GameObjectId ?? 0;
                _actions.ActionsBlocked = false; // Allow only this synchronous stop call.
                try { ui->WeaponState.AutoAttackState.Set(false); }
                finally { _actions.ActionsBlocked = true; }
            }
        }
        else if (_restoreAutosTarget != 0)
        {
            if (ui != null && Service.LocalPlayer is { IsDead: false }
                && Service.TargetManager.Target?.GameObjectId == _restoreAutosTarget)
                ui->WeaponState.AutoAttackState.Set(true);
            _restoreAutosTarget = 0;
        }
        if ((restrictions & ExecutionRestrictions.CancelCast) != 0)
        {
            if (ui != null && Service.LocalPlayer?.IsCasting == true && now >= _nextCancel)
            {
                ui->Hotbar.CancelCast();
                _nextCancel = now.AddMilliseconds(250);
            }
        }
        else _nextCancel = default;
    }

    public void Dispose()
    {
        MechanicExecutionState.Publish(ExecutionRestrictions.None);
        _actions.ActionsBlocked = false;
        _actions.Dispose();
        _movement.Dispose();
    }
}
