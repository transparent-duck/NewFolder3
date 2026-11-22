using global::Dalamud.Game.ClientState.Objects.Enums;
using FFXIVClientStructs.FFXIV.Client.Game.InstanceContent;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine;
using NativeGameObject = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;
using NativeEventObject = FFXIVClientStructs.FFXIV.Client.Game.Object.EventObject;

namespace DeepDungeon.Fsd.Dalamud.GameState;

/// <summary>On-demand native facts for before/after passage and item observations.</summary>
internal static unsafe class DeepDungeonSceneObservation
{
    internal static object? CaptureCurrent()
    {
        var framework = FFXIVClientStructs.FFXIV.Client.Game.Event.EventFramework.Instance();
        return framework == null ? null : Capture(framework->GetInstanceContentDeepDungeon());
    }

    internal static object? Capture(InstanceContentDeepDungeon* dd)
    {
        if (dd == null) return null;
        var items = new object[16];
        for (int slot = 0; slot < items.Length; slot++)
        {
            ref var item = ref dd->Items[slot];
            items[slot] = new { slot, itemId = item.ItemId, count = item.Count,
                flags = item.Flags, usable = item.IsUsable, active = item.IsActive };
        }
        var passages = new List<object>();
        foreach (var obj in Service.GameObjects)
        {
            if (obj.ObjectKind != ObjectKind.EventObj || !PassageLocator.IsPassageBase(obj.BaseId)) continue;
            var native = (NativeGameObject*)obj.Address;
            var eventObject = (NativeEventObject*)obj.Address;
            var group = native->SharedGroupLayoutInstance;
            var instance = (ILayoutInstance*)group;
            passages.Add(new
            {
                actorId = obj.GameObjectId, baseId = obj.BaseId,
                position = new { x = obj.Position.X, y = obj.Position.Y, z = obj.Position.Z },
                targetable = obj.IsTargetable, eventState = native->EventState,
                passageEligible = PassageLocator.IsAvailablePassage(obj),
                visibility = native->Visibility, renderFlags = (ulong)native->RenderFlags,
                targetableFlags = (byte)native->TargetableStatus,
                sharedTimelineState = eventObject->SharedTimelineState,
                arg = eventObject->Arg, eventFlags = eventObject->Flags,
                drawObjectPresent = native->DrawObject != null,
                drawVisible = native->DrawObject != null ? (bool?)native->DrawObject->IsVisible : null,
                sharedGroup = group == null ? null : new
                {
                    active = instance->IsActive, flags1 = instance->Flags1,
                    flags2 = instance->Flags2, flags3 = instance->Flags3,
                    prefabFlags1 = group->PrefabFlags1, prefabFlags2 = group->PrefabFlags2,
                    playingTimelineIndex = group->PlayingTimelineIndex
                }
            });
        }
        return new { capturedAtUtc = DateTime.UtcNow, dungeonId = dd->DeepDungeonId, floor = dd->Floor,
            passageProgress = dd->PassageProgress, activeLayoutIndex = dd->ActiveLayoutIndex,
            layoutInitializationType = dd->LayoutInitializationType, items, passages };
    }
}
