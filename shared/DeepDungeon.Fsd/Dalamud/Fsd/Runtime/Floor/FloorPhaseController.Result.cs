using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using DeepDungeon.Fsd.Dalamud.Runtime.Navigation;
using global::Dalamud.Game.ClientState.Conditions;
using global::Dalamud.Game.ClientState.Objects.Enums;
using global::Dalamud.Game.ClientState.Objects.Types;
using Treasure = FFXIVClientStructs.FFXIV.Client.Game.Object.Treasure;

namespace DeepDungeon.Fsd.Dalamud.Runtime.Floor;

public sealed partial class FloorPhaseController
{
    private readonly HashSet<ulong> _resultOpened = new();
    private IGameObject? _resultChest;
    private DateTime _resultNextTick, _resultStarted, _resultChestStarted, _resultNextInteract, _resultInteractedAt;
    private bool _resultEntered;

    // The first live result room must establish reward/exit object identities before
    // allowing automatic departure. Native completion at 99 alone cannot skip it.
    private unsafe void UpdateResultRoom()
    {
        var context = _ctx;
        var player = Service.LocalPlayer;
        if (context == null || player == null) return;
        var now = DateTime.UtcNow;
        if (now < _resultNextTick) return;
        _resultNextTick = now.AddMilliseconds(500);
        if (!_resultEntered)
        {
            DestroyFloorRuntime(context.Duty.Floor, "result-room");
            _resultEntered = true;
            context.TerminalRoomObserved = true;
            _resultStarted = now;
            RecordReplayEvent("result-room-entered", new
            {
                floor = context.Duty.Floor, dungeonId = context.Duty.DungeonId,
                player = new { player.Position.X, player.Position.Y, player.Position.Z },
                objects = Service.GameObjects.Select(obj => new
                {
                    obj.GameObjectId, obj.BaseId, kind = obj.ObjectKind.ToString(),
                    obj.IsTargetable, name = obj.Name.TextValue,
                    position = new { obj.Position.X, obj.Position.Y, obj.Position.Z }
                }).ToArray()
            });
            Service.Log.Info("[ResultRoom] Native result floor entered; random-room graph, combat and passage disabled.");
        }
        if (Service.Condition[ConditionFlag.InCombat] || player.IsCasting) return;

        // ObjectKind.Treasure is authoritative independently of translated names.
        // Event-object reward coffers need their first live identity/state capture.
        if (_resultChest != null)
        {
            var live = Service.GameObjects.FirstOrDefault(obj => obj.GameObjectId == _resultChest.GameObjectId &&
                obj.EntityId == _resultChest.EntityId && obj.BaseId == _resultChest.BaseId);
            if (live == null)
            {
                StopResultReview("獎勵箱在確認原生開啟狀態前消失；請驗收100層物件與獎勵。", now);
                return;
            }
            var treasure = (Treasure*)live.Address;
            if (_resultInteractedAt != default &&
                treasure->State >= Treasure.TreasureState.Opened)
            {
                _resultOpened.Add(live.GameObjectId);
                RecordReplayEvent("result-chest-opened", new { live.GameObjectId, live.BaseId,
                    state = (byte)treasure->State, flags = (byte)treasure->Flags });
                Service.Log.Info($"[ResultRoom] Reward coffer opened id={live.GameObjectId}, baseId={live.BaseId}, state={treasure->State}; inventory receipt/exit still require live verification");
                _resultChest = null;
                _resultInteractedAt = default;
                _resultStarted = now; // Allow subsequently spawned coffers a full quiet window.
            }
            else if ((now - (_resultInteractedAt == default ? _resultChestStarted : _resultInteractedAt)).TotalSeconds > 60)
            {
                StopResultReview("獎勵箱導航／開啟逾時；已保留100層現場。", now);
                return;
            }
        }
        if (_resultChest == null)
        {
            float nearest = float.MaxValue;
            foreach (var obj in Service.GameObjects)
            {
                if (obj.ObjectKind != ObjectKind.Treasure || !obj.IsTargetable ||
                    _resultOpened.Contains(obj.GameObjectId)) continue;
                var treasure = (Treasure*)obj.Address;
                if (treasure->State != Treasure.TreasureState.Unopened) continue;
                float distance = Vector3.DistanceSquared(player.Position, obj.Position);
                if (distance < nearest) { nearest = distance; _resultChest = obj; }
            }
            if (_resultChest == null)
            {
                _status = "100層：等待獎勵箱物件";
                if ((now - _resultStarted).TotalSeconds >= 10)
                    StopResultReview($"100層已確認開啟 {_resultOpened.Count} 個獎勵箱；請驗收獎勵與出口，FSD 已停止並保留現場。", now);
                return;
            }
            _resultChestStarted = now;
        }
        var navigation = _navHelper!.Navigate(_resultChest.Position, player.Position, 2.5f);
        _status = $"100層：前往獎勵箱 {_resultChest.BaseId}";
        if (navigation is NavigationState.Failed or NavigationState.StuckGiveUp)
        {
            StopResultReview("獎勵房導航失敗；請驗收100層地圖。", now);
            return;
        }
        if (navigation != NavigationState.Arrived || now < _resultNextInteract) return;
        _resultNextInteract = now.AddSeconds(3);
        GameInteraction.InteractWith(_resultChest, 3f, false, out var failure, out bool dispatched);
        if (dispatched && _resultInteractedAt == default) _resultInteractedAt = now;
        RecordReplayEvent("result-chest-interaction", new { _resultChest.GameObjectId,
            _resultChest.BaseId, dispatched, failure });
    }

    private void ResetResultRoom()
    {
        _resultOpened.Clear();
        _resultChest = null;
        _resultEntered = false;
        _resultNextTick = _resultStarted = _resultChestStarted = _resultNextInteract = _resultInteractedAt = default;
    }

    private void StopResultReview(string message, DateTime now)
    {
        _navHelper?.Cancel();
        _ctx!.StatusLine = _status = message;
        _ctx.StatusIsError = true;
        RecordReplayEvent("result-room-review-required", new
        {
            message, at = now, opened = _resultOpened.ToArray(),
            objects = Service.GameObjects.Select(obj => new
            { obj.GameObjectId, obj.BaseId, kind = obj.ObjectKind.ToString(), obj.IsTargetable,
                name = obj.Name.TextValue,
                position = new { obj.Position.X, obj.Position.Y, obj.Position.Z } }).ToArray()
        });
        Service.Log.Info("[ResultRoom] " + message);
    }
}
