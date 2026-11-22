using System;
using System.Numerics;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using DeepDungeon.Fsd.Dalamud;
using FFXIVClientStructs.FFXIV.Client.Game.InstanceContent;

namespace DeepDungeon.Fsd.Dalamud.Movement;

/// <summary>FSD owns selection and lifetime; SpinningMob owns the orbit calculation.</summary>
internal sealed unsafe class SpinBossMovementStrategy(SpinningMob spin) : IBossMovementStrategy
{
    private static readonly TimeSpan UserCancelCooldown = TimeSpan.FromSeconds(2);
    private DateTime _resumeAfterUtc;
    private bool _hadOwnership;

    public string Name => "spin";
    public bool SuppressCombatItems => true;
    public bool RequestsNorthCamera { get; private set; }

    public void Start() => _resumeAfterUtc = DateTime.MinValue;

    public string Update(InstanceContentDeepDungeon* dungeon)
    {
        var start = System.Diagnostics.Stopwatch.GetTimestamp();
        SpinningMob.RecordOrbitTiming(start, 0);
        try { return UpdateCore(dungeon); }
        finally { SpinningMob.RecordOrbitTiming(start, System.Diagnostics.Stopwatch.GetTimestamp()); }
    }

    private string UpdateCore(InstanceContentDeepDungeon* dungeon)
    {
        RequestsNorthCamera = false;
        if (_hadOwnership && !spin.IsOwnedBy(this))
        {
            _hadOwnership = false;
            if (spin.WasCancelledByUser)
                _resumeAfterUtc = DateTime.UtcNow + UserCancelCooldown;
        }

        // A manual command can replace the automatic session. Do not tick or stop it.
        if (spin.IsActive && !spin.IsOwnedBy(this))
            return "Boss floor - yielding to manual spin";
        if (UserMovementInput.IsActive)
        {
            spin.StopOwned(this);
            _hadOwnership = false;
            _resumeAfterUtc = DateTime.UtcNow + UserCancelCooldown;
        }
        if (DateTime.UtcNow < _resumeAfterUtc)
            return "Boss floor - yielding to player input";

        if (!spin.IsOwnedBy(this))
        {
            var player = Service.LocalPlayer;
            if (player == null || player.IsDead)
                return "Boss floor - waiting for player";
            var boss = FindBoss(player.Position);
            if (boss == null || !spin.StartOwned(boss, this))
                return "Boss floor - waiting for spin target";
            _hadOwnership = true;
        }

        if (spin.TryUpdateOwned(this))
        {
            RequestsNorthCamera = true;
            return "Boss floor - approach and orbit active";
        }
        if (spin.WasCancelledByUser)
        {
            _hadOwnership = false;
            _resumeAfterUtc = DateTime.UtcNow + UserCancelCooldown;
            return "Boss floor - yielding to player input";
        }
        return "Boss floor - orbit waiting for movement control";
    }

    public void Stop()
    {
        spin.StopOwned(this);
        _hadOwnership = false;
        RequestsNorthCamera = false;
    }

    private static IBattleChara? FindBoss(Vector3 playerPosition)
    {
        IBattleChara? best = null;
        uint bestMaxHp = 0;
        float bestDistanceSquared = 80f * 80f;
        foreach (var obj in Service.GameObjects)
        {
            if (obj is not IBattleChara enemy || obj.ObjectKind != ObjectKind.BattleNpc ||
                (BattleNpcSubKind)obj.SubKind != BattleNpcSubKind.Combatant ||
                obj.IsDead || !obj.IsTargetable || !enemy.StatusFlags.HasFlag(StatusFlags.Hostile))
                continue;
            float dx = obj.Position.X - playerPosition.X;
            float dz = obj.Position.Z - playerPosition.Z;
            float distanceSquared = dx * dx + dz * dz;
            if (distanceSquared > 80f * 80f) continue;
            if (best == null || enemy.MaxHp > bestMaxHp ||
                enemy.MaxHp == bestMaxHp && distanceSquared < bestDistanceSquared)
            {
                best = enemy;
                bestMaxHp = enemy.MaxHp;
                bestDistanceSquared = distanceSquared;
            }
        }
        return best;
    }
}
