using System;
using System.Collections.Generic;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using MasterPlayer.MechanicRuntime;

namespace DeepDungeon.Fsd.Dalamud.Encounter;

internal static unsafe class MechanicTimelineReader
{
    private static readonly Dictionary<uint, ushort> Timelines = new();
    internal static MechanicTimelineObservation? Capture(Character* character, ulong caster,
        uint action, uint sequence, DateTime now)
    {
        if (character == null) return null;
        // The response identity and actual slot must both match this receipt;
        // a previous animation of the same type is not a timing observation.
        var cast = character->GetCastInfo();
        if (cast == null || cast->ResponseGlobalSequence != sequence || cast->ResponseActionId != action) return null;
        if (!Timelines.TryGetValue(action, out var timelineId))
        {
            var row = Service.DataManager.GameData.GetExcelSheet<Lumina.Excel.Sheets.Action>()?.GetRowOrDefault(action);
            timelineId = (ushort)(row?.AnimationEnd.RowId ?? 0);
            Timelines.Add(action, timelineId);
        }
        if (timelineId == 0) return null;
        var sequencer = &character->Timeline.TimelineSequencer;
        for (uint slot = 0; slot < 14; ++slot)
        {
            if (sequencer->GetSlotTimeline(slot) != timelineId) continue;
            var scheduler = sequencer->GetSchedulerTimeline(slot);
            if (scheduler == null) continue;
            var timestamp = scheduler->TimelineController.CurrentTimestamp;
            //141BFC1D8 sets the actual scheduler multiplier from the request.
            // This is the same field read by the Fall Guys ice observer.
            var speed = *(float*)((byte*)scheduler + 0x240);
            if (!float.IsFinite(timestamp) || timestamp < 0 || !float.IsFinite(speed) || speed < 0) continue;
            return new(caster, action, sequence, timelineId, now, timestamp, speed);
        }
        return null;
    }
}
