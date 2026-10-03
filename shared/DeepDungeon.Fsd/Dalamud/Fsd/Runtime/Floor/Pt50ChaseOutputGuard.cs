using System;
using global::Dalamud.Game.ClientState.Objects.Types;
using FFXIVClientStructs.FFXIV.Client.Game.InstanceContent;

namespace DeepDungeon.Fsd.Dalamud.Runtime.Floor;

/// <summary>Preserve BMR's rotation-dependent chase routing by pausing external attacks, not movement.</summary>
internal unsafe sealed class Pt50ChaseOutputGuard(Action<bool> setSuppressed)
{
    private const uint BossBaseId = 18546;
    private const uint PitAmbushFirst = 43534;
    private IBattleChara? _boss;
    private DateTime _until;
    public bool IsActive { get; private set; }

    public void Update(InstanceContentDeepDungeon* dd, bool enabled)
    {
        if (!enabled || dd == null || dd->DeepDungeonId != 4 || dd->Floor != 50 ||
            Service.LocalPlayer == null || Service.LocalPlayer.IsDead)
        {
            Reset();
            return;
        }
        if (_boss == null)
            foreach (var obj in Service.GameObjects)
                if (obj is IBattleChara battle && battle.BaseId == BossBaseId) { _boss = battle; break; }
        var now = DateTime.UtcNow;
        if (_boss is { IsCasting: true, CastActionId: PitAmbushFirst })
        {
            // First circle plus three observed 2.1-second chases and latency margin.
            var until = now.AddSeconds(Math.Max(0, _boss.TotalCastTime - _boss.CurrentCastTime) + 3 * 2.1 + 0.8);
            if (until > _until) _until = until;
        }
        Set(now < _until);
    }

    public void Reset()
    {
        _until = default;
        _boss = null;
        Set(false);
    }

    private void Set(bool active)
    {
        if (active == IsActive) return;
        IsActive = active;
        setSuppressed(active);
        Service.Log.Info($"[PT50.ChaseOutput] suppressed={active}, until={_until:o}; BMR retains movement ownership");
    }
}
