using System;
using System.Numerics;
using MasterPlayer.MechanicRuntime;
using global::Dalamud.Game.ClientState.Conditions;
using global::Dalamud.Game.ClientState.Objects.Types;
using DeepDungeon.Fsd.Dalamud.Runtime;
using DeepDungeon.Fsd.Dalamud.Runtime.Navigation;

namespace DeepDungeon.Fsd.Dalamud.Runtime.Combat;

/// <summary>
/// Shared combat-assist policy used by both manual banded farming and FullSelfDelving controllers.
/// Encapsulates target selection. Attacks are owned by the selected rotation plugin.
/// Run-scoped state is maintained between calls.
/// </summary>
internal sealed class CombatAssistPolicy
{
    private readonly ICombatTargetProvider _encounter = new EncounterTargetProvider();
    private readonly ICombatTargetProvider _default = new DefaultTargetProvider();
    private CombatTargetChoice _decision;
    private DateTime _decisionFrame;
    internal string? TargetSource => _decision.Source;
    internal ulong SelectedId { get; private set; }

    internal void Suspend()
    {
        // Release only our last selection; a user's unrelated target is not ours.
        if (SelectedId != 0 && Service.TargetManager.Target?.GameObjectId == SelectedId)
            Service.TargetManager.Target = null;
        SelectedId = 0;
        _decision = default;
        _decisionFrame = default;
    }

    // Cache the navigation decision once per frame. Attack-side sight filtering
    // must not replace it, so navigation can still approach an obstructed target.
    private CombatTargetChoice Select(in CombatTargetRequest request)
    {
        var frame = Service.Framework.LastUpdateUTC;
        if (_decisionFrame == frame) return _decision;
        _decisionFrame = frame;
        _decision = Choose(request);
        SelectedId = _decision.Target?.GameObjectId ?? 0;
        return _decision;
    }

    private CombatTargetChoice Choose(in CombatTargetRequest request) =>
        _encounter.TrySelect(request, out var choice) || _default.TrySelect(request, out choice)
            ? choice
            : default;

    // Filter the attack sidecar only. Navigation can still approach its
    // independently selected target when that target is behind a wall.
    private CombatTargetChoice SelectAttack(in CombatTargetRequest request, Vector3 position)
    {
        if (!request.BossFloor && request.Context?.ClearingTargetId is { } targetId)
        {
            var selected = Service.GameObjects.SearchById(targetId) as IBattleChara;
            if (selected is { IsDead: false, IsTargetable: true, CurrentHp: > 0 } &&
                CombatPositionNavigation.CheckAttack(position, selected.Position, request.Range).Allowed)
                return new(selected, "clearing-target");
            // An obstructed chase target does not own the attack channel.
            var nearby = Choose(request with { SightOrigin = position });
            return nearby.Target == null ? nearby with { HoldFire = true } : nearby;
        }

        var choice = Select(request);
        if (choice.Target is not { } target || !CombatTargetRequest.IsSightBlocked(position, target.Position))
            return choice;
        choice = Choose(request with { SightOrigin = position });
        return choice.Target == null ? choice with { HoldFire = true } : choice;
    }

    private static void Apply(in CombatTargetChoice decision)
    {
        if (decision.Target is { IsDead: false, IsTargetable: true } target)
        {
            if (Service.TargetManager.Target?.GameObjectId != target.GameObjectId)
                Service.TargetManager.Target = target;
        }
        else if (decision.HoldFire && Service.TargetManager.Target is IBattleNpc)
            Service.TargetManager.Target = null;
    }

    internal (Vector3 Position, float Radius)? SelectForNavigation(RunContext ctx, EncounterPlanningSnapshot snapshot)
    {
        var choice = Select(new(ctx, true, Service.Condition[ConditionFlag.InCombat], ctx.Duty.PassageOpen,
            GetEngageRange(), snapshot));
        Apply(choice);
        return choice.Target is { } target ? (target.Position, target.HitboxRadius) : null;
    }

    private uint _rangeJob;
    private float _engageRange;
    private DateTime _nextDiagnosticAt;
    private string _lastDiagnosticStatus = string.Empty;
    private ulong _lastDiagnosticTarget;

    public void Tick(RunContext context, bool isBossFloor, bool passageOpen, out string status)
    {
        status = "Select: idle";
        try
        {
            var player = Service.LocalPlayer;
            if (player == null || player.IsDead) return;
            bool inCombat = Service.Condition[ConditionFlag.InCombat];
            float range = GetEngageRange();
            var choice = SelectAttack(new(context, isBossFloor, inCombat, passageOpen, range,
                context.Encounters.Read().Encounter), player.Position);
            var target = choice.Target;
            SelectedId = target?.GameObjectId ?? 0;
            Apply(choice);
            if (target == null)
            {
                if (!inCombat && !passageOpen && Service.TargetManager.Target is IBattleChara previous &&
                    (!context.IsNewCombatTargetAllowed(previous) || context.IsCombatTargetSuppressed(previous.GameObjectId)))
                    Service.TargetManager.Target = null;
                status = $"{choice.Source}: {(choice.HoldFire ? "hold fire" : "no target")}";
            }
            else
            {
                status = $"{choice.Source} →{target.Name.TextValue}";
                if (!inCombat && !passageOpen && Vector2.DistanceSquared(new(player.Position.X, player.Position.Z),
                    new(target.Position.X, target.Position.Z)) <= range * range)
                    context.PrepareCombat(target.GameObjectId);
            }
            var now = DateTime.UtcNow;
            if (now < _nextDiagnosticAt && status == _lastDiagnosticStatus && SelectedId == _lastDiagnosticTarget) return;
            _nextDiagnosticAt = now.AddSeconds(5);
            _lastDiagnosticStatus = status;
            _lastDiagnosticTarget = SelectedId;
            context.RecordDiagnostic?.Invoke("combat-assist-state", new {
                floor = context.Duty.Floor, status, autoSelect = true,
                inCombat, passageOpen, engageRange = range,
                currentTargetId = Service.TargetManager.Target?.GameObjectId,
                selectedTargetId = SelectedId, targetHp = target?.CurrentHp,
                clearingTargetId = context.ClearingTargetId
            });
        }
        catch (Exception ex)
        {
            status = "Select: error";
            context.RecordDiagnostic?.Invoke("combat-assist-error", new { error = ex.ToString() });
            Service.Log.Error($"[CombatAssistPolicy] Tick error: {ex}");
        }
    }

    internal float GetEngageRange()
    {
        var job = Service.LocalPlayer?.ClassJob;
        uint id = job?.RowId ?? 0;
        if (id != _rangeJob || _engageRange == 0)
        {
            _rangeJob = id;
            _engageRange = job?.Value.Role is 3 or 4 or 5 ? 25f : 3f;
        }
        return _engageRange;
    }
}
