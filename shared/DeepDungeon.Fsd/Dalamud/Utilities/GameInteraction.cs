using System;
using global::Dalamud.Game.ClientState.Objects.Types;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using Obj = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;

namespace DeepDungeon.Fsd.Dalamud
{
    internal static class GameInteraction
    {
        public static unsafe bool InteractWith(IGameObject? obj, float maxDistance = 3.0f, bool force = false)
        {
            try
            {
                if (obj == null) return false;
                var lp = Service.LocalPlayer;
                if (lp == null) return false;

                // distance precheck (client-side UX)
                var dx = obj.Position.X - lp.Position.X;
                var dz = obj.Position.Z - lp.Position.Z;
                var dist2D = MathF.Sqrt(dx * dx + dz * dz);
                if (!force && dist2D > maxDistance) return false;

                return InteractWith(obj.GameObjectId, force);
            }
            catch (Exception ex)
            {
                Service.Log.Error($"[GameInteraction] InteractWith(IGameObject) error: {ex}");
                return false;
            }
        }

        public static unsafe bool InteractWith(ulong gameObjectId, bool force = false)
        {
            try
            {
                var lp = Service.LocalPlayer;
                if (lp == null) return false;

                var gom = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObjectManager.Instance();
                if (gom == null) return false;

                var target = gom->Objects.GetObjectByGameObjectId(gameObjectId);
                if (target == null) return false;

                var playerObj = (Obj*)lp.Address;
                if (playerObj == null) return false;

                // Range check: treasures have no strict client-side check; other objects use EventFramework
                if (!force)
                {
                    var isTreasure = target->ObjectKind == FFXIVClientStructs.FFXIV.Client.Game.Object.ObjectKind.Treasure;
                    if (!isTreasure)
                    {
                        var inRange = EventFramework.Instance()->CheckInteractRange(playerObj, target, 1, false);
                        if (!inRange) return false;
                    }
                }

                return TargetSystem.Instance()->InteractWithObject(target) != 0;
            }
            catch (Exception ex)
            {
                Service.Log.Error($"[GameInteraction] InteractWith(id) error: {ex}");
                return false;
            }
        }
    }
}




