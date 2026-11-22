using System;
using global::Dalamud.Game.ClientState.Conditions;
using global::Dalamud.Game.ClientState.Objects.Types;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using Obj = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;

namespace DeepDungeon.Fsd.Dalamud
{
    /// <summary>Bounded observation of one InteractWith dispatch — the raw
    /// fields the native path actually saw. Null RangeAllowed means the
    /// range check never ran (treasure / force), NOT that it passed.</summary>
    internal readonly record struct GameInteractionTrace(
        byte? NativeKind = null,
        bool? RangeAllowed = null,
        bool NativeCallEntered = false,
        ulong? RawReturn = null);

    internal static class GameInteraction
    {
        public static unsafe bool InteractWith(IGameObject? obj, float maxDistance = 3.0f, bool force = false)
            => InteractWith(obj, maxDistance, force, out _);

        public static unsafe bool InteractWith(IGameObject? obj, float maxDistance, bool force, out string failure)
            => InteractWith(obj, maxDistance, force, out failure, out _);

        public static unsafe bool InteractWith(IGameObject? obj, float maxDistance, bool force, out string failure, out bool dispatched)
            => InteractWith(obj, maxDistance, force, out failure, out dispatched, out _);

        public static unsafe bool InteractWith(IGameObject? obj, float maxDistance, bool force, out string failure, out bool dispatched, out GameInteractionTrace trace)
        {
            dispatched = false;
            failure = "object-unavailable";
            trace = default;
            try
            {
                if (obj == null) return false;
                var lp = Service.LocalPlayer;
                if (lp == null) { failure = "player-unavailable"; return false; }

                // distance precheck (client-side UX)
                var dx = obj.Position.X - lp.Position.X;
                var dz = obj.Position.Z - lp.Position.Z;
                var dist2D = MathF.Sqrt(dx * dx + dz * dz);
                if (!force && dist2D > maxDistance) { failure = "horizontal-range-rejected"; return false; }

                return InteractWith(obj.GameObjectId, force, out failure, out dispatched, out trace);
            }
            catch (Exception ex)
            {
                Service.Log.Error($"[GameInteraction] InteractWith(IGameObject) error: {ex}");
                failure = "exception:" + ex.Message;
                return false;
            }
        }

        public static unsafe bool InteractWith(ulong gameObjectId, bool force = false)
            => InteractWith(gameObjectId, force, out _, out _, out _);

        private static unsafe bool InteractWith(ulong gameObjectId, bool force, out string failure, out bool dispatched, out GameInteractionTrace trace)
        {
            dispatched = false;
            failure = "player-unavailable";
            trace = default;
            try
            {
                if (MasterPlayer.MechanicRuntime.MechanicExecutionState.ActionsBlocked)
                { failure = "mechanic-action-restriction"; return false; }
                var lp = Service.LocalPlayer;
                if (lp == null) return false;

                var gom = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObjectManager.Instance();
                if (gom == null) { failure = "object-manager-unavailable"; return false; }

                var target = gom->Objects.GetObjectByGameObjectId(gameObjectId);
                if (target == null) { failure = "native-target-unavailable"; return false; }
                trace = trace with { NativeKind = (byte)target->ObjectKind };

                var playerObj = (Obj*)lp.Address;
                if (playerObj == null) { failure = "native-player-unavailable"; return false; }

                // Range check: treasures have no strict client-side check; other objects use EventFramework
                if (!force)
                {
                    var isTreasure = target->ObjectKind == FFXIVClientStructs.FFXIV.Client.Game.Object.ObjectKind.Treasure;
                    if (!isTreasure)
                    {
                        var inRange = EventFramework.Instance()->CheckInteractRange(playerObj, target, 1, false);
                        trace = trace with { RangeAllowed = inRange };
                        if (!inRange) { failure = "native-interaction-range-rejected"; return false; }
                    }
                }

                trace = trace with { NativeCallEntered = true };
                var result = TargetSystem.Instance()->InteractWithObject(target);
                trace = trace with { RawReturn = result };
                // This ulong return is not an acknowledgement: PT coffers can open after returning zero.
                // Track issuance separately; the exact chest's fresh native state confirms completion.
                dispatched = true;
                failure = result == 0 ? "native-interaction-returned-zero" : string.Empty;
                return result != 0;
            }
            catch (Exception ex)
            {
                Service.Log.Error($"[GameInteraction] InteractWith(id) error: {ex}");
                failure = "exception:" + ex.Message;
                return false;
            }
        }

        /// <summary>Point-in-time player/action state for throttled
        /// interaction diagnostics — observation only, never an
        /// eligibility predicate. Null ActionManager fields report
        /// "unknown", not zeros.</summary>
        internal static unsafe string DescribePlayerInteractionState()
        {
            var inCombat = Service.Condition[ConditionFlag.InCombat];
            var casting = Service.Condition[ConditionFlag.Casting];
            var occupied = Service.Condition[ConditionFlag.OccupiedInEvent];
            var playerPresent = Service.LocalPlayer != null;
            var am = ActionManager.Instance();
            return $"playerPresent={playerPresent} inCombat={inCombat} casting={casting} " +
                $"occupiedInEvent={occupied} " +
                $"animationLock={(am != null ? am->AnimationLock.ToString("0.###") : "unknown")} " +
                $"actionQueued={(am != null ? am->ActionQueued.ToString() : "unknown")} " +
                $"queuedActionId={(am != null ? am->QueuedActionId.ToString() : "unknown")} " +
                $"castActionId={(am != null ? am->CastActionId.ToString() : "unknown")} " +
                $"castTimeElapsed={(am != null ? am->CastTimeElapsed.ToString("0.###") : "unknown")} " +
                $"castTimeTotal={(am != null ? am->CastTimeTotal.ToString("0.###") : "unknown")}";
        }
    }
}
