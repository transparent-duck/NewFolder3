using System.Numerics;
using global::Dalamud.Game.ClientState.Objects.Types;
using DeepDungeon.Fsd.Dalamud.Runtime.Helpers;
using DeepDungeon.Fsd.Dalamud.Runtime.Navigation;
using MasterPlayer.MechanicRuntime;

namespace DeepDungeon.Fsd.Dalamud.Runtime.Combat;

internal readonly record struct CombatTargetRequest(RunContext? Context, bool BossFloor, bool InCombat,
    bool PassageOpen, float Range, EncounterPlanningSnapshot? Snapshot)
{
    public Vector3? SightOrigin { get; init; }
    public bool Allowed(IBattleChara target) => Context?.IsCombatTargetSuppressed(target.GameObjectId) != true &&
        Context?.IsNewCombatTargetAllowed(target) != false &&
        (SightOrigin is not { } origin || !IsSightBlocked(origin, target.Position));

    internal static bool IsSightBlocked(Vector3 position, Vector3 target) =>
        CombatPositionNavigation.SampleSight(position, target).Blocked;
}

internal readonly record struct CombatTargetChoice(IBattleChara? Target, string Source, bool HoldFire = false);

internal interface ICombatTargetProvider
{
    // A handled decision can explicitly withhold attack. Only no advice falls through.
    bool TrySelect(in CombatTargetRequest request, out CombatTargetChoice choice);
}

internal sealed class EncounterTargetProvider : ICombatTargetProvider
{
    public bool TrySelect(in CombatTargetRequest request, out CombatTargetChoice choice)
    {
        choice = default;
        if (request.Snapshot is not { BossDead: false, Hints: { HasTargetDirective: true } hints } snapshot ||
            snapshot.Territory != (ushort)Service.ClientState.TerritoryType ||
            DateTime.UtcNow - snapshot.CapturedAt > TimeSpan.FromMilliseconds(500))
            return false;

        IBattleChara? target = null;
        if (hints.AttackTarget is { } attack)
        {
            var actor = attack.Id <= uint.MaxValue ? Service.GameObjects.SearchByEntityId((uint)attack.Id) : null;
            target = (actor ?? Service.GameObjects.SearchById(attack.Id)) as IBattleNpc;
            if (target is not { IsDead: false, IsTargetable: true, CurrentHp: > 0 } || !request.Allowed(target))
                target = null;
        }
        choice = new(target, "encounter", target == null);
        return true;
    }
}

internal sealed class DefaultTargetProvider : ICombatTargetProvider
{
    public bool TrySelect(in CombatTargetRequest request, out CombatTargetChoice choice)
    {
        var q = request;
        if (q.Range <= 0 || !float.IsFinite(q.Range))
        {
            choice = new(null, "default");
            return true;
        }
        IBattleChara? target;
        if (q.InCombat && q.BossFloor)
            target = PickBossTarget(q.Context, q.Allowed);
        else if (q.InCombat)
        {
            var excluded = CombatTargetingHelpers.PickExcludedAggroTarget(q.Context, q.Allowed);
            if (excluded != null)
            {
                choice = new(excluded, "excluded-aggro");
                return true;
            }
            target = CombatTargetingHelpers.PickHostileHighestHP(q.Range, true, true, q.Allowed);
            target ??= CombatTargetingHelpers.PickNearestAggroedHostile(q.Allowed);
        }
        else
            target = q.PassageOpen ? null : CombatTargetingHelpers.PickNearestHostile(q.Range, out _, q.Allowed);
        choice = new(target, "default");
        return true;
    }

    private DateTime _nextArenaCenterReadAt;
    private int _arenaFloor = -1;
    private Vector2 _arenaCenter;
    private ulong _lastBossTargetId;

    private IBattleChara? PickBossTarget(RunContext? context, Func<IBattleChara, bool> predicate)
    {
        var now = DateTime.UtcNow;
        int floor = context?.Duty.Floor ?? 0;
        if (_arenaFloor != floor || now >= _nextArenaCenterReadAt)
        {
            _arenaFloor = floor;
            _nextArenaCenterReadAt = now.AddSeconds(1);
            _arenaCenter = CombatTargetingHelpers.BossTargetFallbackCenter(predicate);
        }
        var target = CombatTargetingHelpers.PickBossTarget(_arenaCenter, predicate);
        ulong id = target?.GameObjectId ?? 0;
        if (id != _lastBossTargetId)
        {
            _lastBossTargetId = id;
            Service.Log.Info($"[CombatAssist] Boss target floor={floor}, id={id}, hp={target?.CurrentHp}, maxHp={target?.MaxHp}, position={target?.Position}, center={_arenaCenter}, centerSource=add-centroid, priority=lowest-max-hp-then-center");
        }
        return target;
    }

}
