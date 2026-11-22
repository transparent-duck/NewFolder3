using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Game.ClientState.Objects.Types;
using MasterPlayer.MechanicRuntime;
using NativeActionManager = FFXIVClientStructs.FFXIV.Client.Game.ActionManager;
using NativeBattleChara = FFXIVClientStructs.FFXIV.Client.Game.Character.BattleChara;
using NativeCharacter = FFXIVClientStructs.FFXIV.Client.Game.Character.Character;
using NativeGameObject = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;

namespace DeepDungeon.Fsd.Dalamud.Encounter;

/// <summary>
/// Samples the live game world into the vendored runtime's feed DTOs.
/// Field semantics follow the MechanicWorldFeed contract (BSD Hosting layer);
/// the read side uses our own Service facade only.
/// </summary>
internal static class MechanicWorldSampler
{
    private const byte SpellActionType = 1;
    private static ushort _cachedTerritory;
    private static ushort _cachedCfc;
    private static readonly Dictionary<uint, bool> GroundTargetedActions = new();

    internal static ulong Id(IGameObject? obj)
    {
        if (obj == null)
            return 0;
        var id = obj.EntityId is not (0 or 0xE0000000) ? (ulong)obj.EntityId : obj.GameObjectId;
        return id is 0 or 0xE0000000 || (id >> 56) == 0xFF ? 0 : id;
    }

    public static unsafe MechanicWorldFrame Capture(MechanicCastLocationCache? castLocations, DateTime now,
        DisplacementTimingTracker? displacements = null)
    {
        var territory = (ushort)Service.ClientState.TerritoryType;
        var localPlayer = Service.LocalPlayer;
        var incoming = new List<MechanicActorIncomingEffects>();
        var actors = CaptureActors(incoming, castLocations, now);
        var clocks = new List<MechanicTimelineObservation>();
        if (displacements is { HasPendingReceipts: true })
            foreach (var actor in actors)
                if (displacements.TryGetTimelineAction(actor.EntityId, out var action, out var sequence)
                    && Service.GameObjects.SearchByEntityId((uint)actor.EntityId) is { } source
                    && MechanicTimelineReader.Capture((NativeCharacter*)source.Address, actor.EntityId, action, sequence, now) is { } clock)
                    clocks.Add(clock);
        var waymarks = new (byte Id, Vector3? Pos)[8];
        return new(territory, ResolveCfc(territory), Id(localPlayer), Id(Service.TargetManager.Target),
            CaptureParty(localPlayer), waymarks, actors)
        { IncomingEffects = [.. incoming], ActionTimelines = clocks.ToArray(),
            Displacement = localPlayer == null ? null : DeepDungeon.Fsd.Dalamud.Coord.NativeDisplacementReader.Capture((NativeCharacter*)localPlayer.Address, now) };
    }

    private static unsafe MechanicActorSnapshot[] CaptureActors(List<MechanicActorIncomingEffects> incoming,
        MechanicCastLocationCache? castLocations, DateTime now)
    {
        var table = Service.GameObjects;
        var actors = new List<MechanicActorSnapshot>(table.Length);
        for (var i = 0; i < table.Length; ++i)
        {
            IGameObject? obj;
            try
            {
                obj = table[i];
            }
            catch
            {
                continue;
            }
            if (obj == null || obj.Address == nint.Zero)
                continue;
            var id = Id(obj);
            if (id == 0)
                continue;

            var native = (NativeGameObject*)obj.Address;
            var chrNative = native->IsCharacter() ? (NativeCharacter*)native : null;
            var battle = obj as IBattleChara;
            var battleNative = battle != null ? (NativeBattleChara*)obj.Address : null;
            var isAlly = chrNative == null
                || NativeActionManager.ClassifyTarget(chrNative) != NativeActionManager.TargetCategory.Enemy;

            MechanicCastSnapshot? cast = null;
            var statuses = Array.Empty<MechanicStatusDto>();
            uint curHp = 0, maxHp = 0, curMp = 0, maxMp = 0;
            byte shield = 0, modelState = 0, animState1 = 0, animState2 = 0;
            uint nameId = 0, classJobId = 0;
            var level = 0;
            var inCombat = false;
            uint tetherId = 0;
            ulong tetherTargetId = 0;
            if (battle != null && battleNative != null)
            {
                curHp = battle.CurrentHp;
                maxHp = battle.MaxHp;
                curMp = battle.CurrentMp;
                maxMp = battle.MaxMp;
                shield = battle.ShieldPercentage;
                nameId = battle.NameId;
                classJobId = battle.ClassJob.RowId;
                level = battle.Level;
                inCombat = battleNative->InCombat;
                modelState = battleNative->Timeline.ModelState;
                animState1 = battleNative->Timeline.AnimationState[0];
                animState2 = battleNative->Timeline.AnimationState[1];
                ref readonly var tether = ref battleNative->Vfx.Tethers[0];
                if (tether.Id != 0)
                {
                    tetherId = tether.Id;
                    tetherTargetId = tether.TargetId;
                }
                var castInfo = battleNative->GetCastInfo();
                if (castInfo != null && castInfo->IsCasting && castInfo->ActionId != 0
                    && float.IsFinite(castInfo->CurrentCastTime) && float.IsFinite(castInfo->TotalCastTime))
                {
                    var rotation = float.IsFinite(battleNative->CastRotation) ? battleNative->CastRotation : 0f;
                    var location = castInfo->TargetLocation;
                    var elapsed = Math.Clamp(castInfo->CurrentCastTime, 0, Math.Max(0, castInfo->TotalCastTime));
                    if (castLocations?.TryGet(obj.EntityId, castInfo->ActionId, castInfo->ActionType,
                            now, elapsed, out var packetPosition, out var packetRotation) == true)
                    {
                        location = packetPosition;
                        rotation = packetRotation;
                    }
                    else
                    {
                        if (castInfo->ActionType != SpellActionType || !IsGroundTargeted(castInfo->ActionId))
                            location = Service.GameObjects.SearchById(castInfo->TargetId)?.Position ?? obj.Position;
                        castLocations?.RememberFallback(obj.EntityId, castInfo->ActionId, castInfo->ActionType,
                            now, elapsed, castInfo->TotalCastTime, location, rotation);
                    }
                    cast = new(castInfo->ActionId, castInfo->ActionType == SpellActionType, castInfo->TargetId,
                        rotation, location, castInfo->TotalCastTime, castInfo->Interruptible)
                    {
                        ElapsedTime = elapsed
                    };
                }
                else castLocations?.Forget(obj.EntityId);
                var manager = battleNative->GetStatusManager();
                var statusCount = manager == null ? 0 : manager->NumValidStatuses;
                if (statusCount > 0)
                {
                    var list = new List<MechanicStatusDto>(statusCount);
                    for (var s = 0; s < manager->Status.Length; ++s)
                    {
                        ref readonly var status = ref manager->Status[s];
                        if (status.StatusId != 0)
                            list.Add(new(s, status.StatusId, status.Param, (uint)status.SourceObject.Id, status.RemainingTime));
                    }
                    statuses = list.ToArray();
                }
            }

            actors.Add(new(id, obj.BaseId, i, native->LayoutId, obj.Name.TextValue, nameId,
                (byte)obj.ObjectKind, obj.SubKind, classJobId, level, obj.Position, obj.Rotation, obj.HitboxRadius,
                curHp, maxHp, shield, curMp, maxMp,
                obj.IsTargetable, isAlly, obj.IsDead, inCombat,
                obj.OwnerId, obj.TargetObjectId, native->FateId, (int)native->RenderFlags, native->EventState,
                modelState, animState1, animState2, tetherId, tetherTargetId, cast, statuses));

            if (chrNative != null)
            {
                var aeh = chrNative->GetActionEffectHandler();
                if (aeh != null)
                {
                    List<MechanicIncomingEffectSlot>? slots = null;
                    for (var s = 0; s < 32; ++s)
                    {
                        ref readonly var entry = ref aeh->IncomingEffects[s];
                        if (entry.GlobalSequence == 0)
                            continue;
                        var raw = new ulong[8];
                        var effectsCopy = entry.Effects;
                        var src = (ulong*)&effectsCopy;
                        for (var j = 0; j < 8; ++j)
                            raw[j] = src[j];
                        (slots ??= new(2)).Add(new(s, entry.GlobalSequence, entry.TargetIndex, entry.ActionType,
                            entry.ActionId, entry.Source, entry.TargetConfirmed, raw));
                    }
                    if (slots != null)
                        incoming.Add(new(id, [.. slots]));
                }
            }
        }
        return actors.ToArray();
    }

    private static bool IsGroundTargeted(uint actionId)
    {
        if (GroundTargetedActions.TryGetValue(actionId, out var value)) return value;
        value = Service.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Action>()
            ?.GetRowOrDefault(actionId)?.TargetArea ?? false;
        if (GroundTargetedActions.Count >= 2048) GroundTargetedActions.Clear();
        GroundTargetedActions[actionId] = value;
        return value;
    }

    private static unsafe MechanicPartySlotDto[] CaptureParty(IGameObject? localPlayer)
    {
        var localId = Id(localPlayer);
        if (localId == 0) return [];
        var slots = new List<MechanicPartySlotDto>(8) { new(0, 0, localId, localPlayer!.Name.TextValue) };
        var list = Service.PartyList;
        for (var i = 0; i < list.Length && slots.Count < 8; ++i)
        {
            var member = list[i];
            if (member == null || member.EntityId is 0 or 0xE0000000) continue;
            if (member.EntityId == localId)
                slots[0] = new(0, member.ContentId, localId, member.Name.TextValue);
            else
                slots.Add(new(slots.Count, member.ContentId, member.EntityId, member.Name.TextValue));
        }
        if (Service.ClientState.TerritoryType is 1038 or 995)
        {
            var ui = FFXIVClientStructs.FFXIV.Client.Game.UI.UIState.Instance();
            if (ui != null && ui->Buddy.DutyHelperInfo.HasHelpers && ui->Buddy.DutyHelperInfo.DutyHelpers != null)
            {
                for (int i = 0; i < 7 && slots.Count < 8; ++i)
                {
                    var id = ui->Buddy.DutyHelperInfo.DutyHelpers[i].EntityId;
                    if (id is 0 or 0xE0000000) continue;
                    var duplicate = false;
                    foreach (var slot in slots) duplicate |= slot.InstanceId == id;
                    if (!duplicate && Service.GameObjects.SearchById(id) is { } actor)
                        slots.Add(new(slots.Count, 0, id, actor.Name.TextValue));
                }
            }
        }
        return slots.ToArray();
    }

    internal static ushort ResolveCfc(ushort territory)
    {
        if (_cachedTerritory == territory)
            return _cachedCfc;
        var cfc = (ushort)0;
        try
        {
            var row = Service.DataManager.GetExcelSheet<Lumina.Excel.Sheets.TerritoryType>()?.GetRowOrDefault(territory);
            if (row?.ContentFinderCondition.ValueNullable is { } cfcRow)
                cfc = (ushort)cfcRow.RowId;
        }
        catch
        {
        }
        _cachedTerritory = territory;
        _cachedCfc = cfc;
        return cfc;
    }
}
