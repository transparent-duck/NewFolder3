using DeepDungeon.Fsd.Core;
using System.Numerics;
using DeepDungeon.Fsd.Dalamud.Runtime.Navigation;
using FFXIVClientStructs.FFXIV.Client.Game.InstanceContent;
using global::Dalamud.Game.ClientState.Conditions;
using global::Dalamud.Game.ClientState.Objects.Enums;
using global::Dalamud.Game.ClientState.Objects.Types;

namespace DeepDungeon.Fsd.Dalamud.Runtime.Floor;

internal sealed partial class TerminalRoomController
{
    private DateTime _terminalTransferStarted, _terminalTransferNextTick, _terminalTransferNextInteract;
    private bool _terminalTransferLandingObserved;

    public unsafe bool TryUpdatePt99ResultTransfer(InstanceContentDeepDungeon* dd)
    {
        if (!Pt99TransitionPolicy.CanApproach(dd->DeepDungeonId, dd->Floor,
            Service.Condition[ConditionFlag.InCombat], _ctx?.Duty.PassageOpen == true)) return false;
        var now = DateTime.UtcNow;
        if (now < _terminalTransferNextTick) return _terminalTransferStarted != default;
        _terminalTransferNextTick = now.AddMilliseconds(500);
        IGameObject? portal = null;
        IGameObject? passage = null;
        foreach (var obj in Service.GameObjects)
        {
            if (obj.ObjectKind == ObjectKind.EventObj && obj.BaseId == Pt99TransitionPolicy.ResultPortalBaseId && obj.IsTargetable)
                portal = obj;
            else if (obj.ObjectKind == ObjectKind.EventObj && obj.BaseId == Pt99TransitionPolicy.PassageBaseId)
                passage = obj;
        }
        if (portal == null) return false;
        var player = Service.LocalPlayer;
        if (player == null || _ctx == null) return false;
        if (_terminalTransferStarted == default)
        {
            _terminalTransferStarted = now;
            Service.Log.Info($"[PT99.Transfer] Post-boss portal observed: id={portal.GameObjectId}, baseId={portal.BaseId}, position={portal.Position}; awaiting native floor100 after interaction.");
            RecordReplayEvent("pt99-result-portal-observed", new
            {
                floor = dd->Floor, portal.GameObjectId, portal.BaseId,
                position = new { portal.Position.X, portal.Position.Y, portal.Position.Z }
            });
        }
        if ((now - _terminalTransferStarted).TotalSeconds > 60)
        {
            StopResultReview("99層通關後前往終局房間逾時；已保留副本，請檢查傳送物件。", now);
            return true;
        }
        if (player.IsCasting || Service.Condition[ConditionFlag.OccupiedInCutSceneEvent] ||
            Service.Condition[ConditionFlag.OccupiedInEvent] || Service.Condition[ConditionFlag.Jumping] ||
            Service.Condition[ConditionFlag.Jumping61])
        {
            _navHelper?.Cancel();
            return true;
        }
        if (passage != null && Pt99TransitionPolicy.OnPassageSide(
            Vector3.Distance(player.Position, portal.Position), Vector3.Distance(player.Position, passage.Position)))
        {
            if (!_terminalTransferLandingObserved)
            {
                _terminalTransferLandingObserved = true;
                _navHelper?.Cancel();
                Service.Log.Info($"[PT99.Transfer] Jump landing observed at {player.Position}; approaching passage {passage.GameObjectId} at {passage.Position}.");
                RecordReplayEvent("pt99-result-jump-landing", new
                {
                    passage.GameObjectId, passage.BaseId,
                    player = new { player.Position.X, player.Position.Y, player.Position.Z },
                    passage = new { passage.Position.X, passage.Position.Y, passage.Position.Z }
                });
            }
            var passageNavigation = _navHelper!.Navigate(ResolvePassageWalkingPosition(passage.Position), player.Position, 0.5f);
            Status = _ctx.StatusLine = "99層已通關：跨越峽谷後前往傳送裝置，等待100層";
            if (passageNavigation is NavigationState.Failed or NavigationState.StuckGiveUp)
                StopResultReview("99層跨越峽谷後的傳送裝置導航失敗；已保留副本。", now);
            return true;
        }
        var navigation = _navHelper!.Navigate(portal.Position, player.Position, 2.5f);
        Status = _ctx.StatusLine = "99層已通關：前往終局房間傳送物件";
        if (navigation is NavigationState.Failed or NavigationState.StuckGiveUp)
        {
            StopResultReview("99層終局房間傳送物件導航失敗；已保留副本。", now);
            return true;
        }
        if (navigation != NavigationState.Arrived || now < _terminalTransferNextInteract) return true;
        _terminalTransferNextInteract = now.AddSeconds(3);
        GameInteraction.InteractWith(portal, 3f, false, out var failure, out bool dispatched);
        if (dispatched) _navHelper.Cancel();
        Service.Log.Info($"[PT99.Transfer] Portal interaction: id={portal.GameObjectId}, dispatched={dispatched}, reason={failure}; waiting for native result floor.");
        RecordReplayEvent("pt99-result-portal-interaction", new { portal.GameObjectId, portal.BaseId, dispatched, failure });
        return true;
    }
}
