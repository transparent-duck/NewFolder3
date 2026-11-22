using BossMod;
using static BossMod.Components.GenericKnockback;

namespace MasterPlayer.MechanicRuntime;

/// <summary>Session-owned receipt identities. Forecasts select an approach;
/// received effects bind native parameters and the actual animation clock.</summary>
public sealed class DisplacementTimingTracker
{
    public bool HasPendingReceipts => _receipts.Count != 0;
    private readonly Dictionary<(ulong Caster, uint Action), KnockbackPrediction> _forecasts = new();
    private readonly List<Receipt> _receipts = new();
    private NativeDisplacementObservation? _native;
    private MechanicTimelineObservation[] _timelines = [];
    private ushort _territory;
    private ulong _player;
    private sealed class Receipt(KnockbackPrediction prediction, uint sequence, DateTime received,
        Vector2 feet, double duration, MechanicTimelineObservation? timeline, DateTime forecastAt)
    {
        public readonly KnockbackPrediction Prediction = prediction;
        public readonly uint Sequence = sequence;
        public readonly DateTime Received = received;
        public readonly Vector2 Feet = feet;
        public readonly double Duration = duration;
        public readonly DateTime ForecastAt = forecastAt;
        public MechanicTimelineObservation? Timeline = timeline;
        public bool NativeSeen;
    }

    public bool TryGetTimelineAction(ulong caster, out uint action, out uint sequence)
    {
        foreach (var receipt in _receipts)
            if (receipt.Prediction.Source.ActorID == caster && !receipt.NativeSeen)
            { action = receipt.Prediction.Source.ActionID; sequence = receipt.Sequence; return true; }
        action = sequence = 0; return false;
    }

    internal void ObserveFrame(MechanicWorldFrame frame, DateTime now)
    {
        if (_territory != frame.Territory || _player != frame.LocalPlayerId)
        { _forecasts.Clear(); _receipts.Clear(); }
        _territory = frame.Territory; _player = frame.LocalPlayerId;
        _native = frame.Displacement; _timelines = frame.ActionTimelines;
        for (var i = _receipts.Count - 1; i >= 0; --i)
        {
            var r = _receipts[i];
            // Native release retires this execution. A stale packet cannot
            // keep a character waiting on a second local completion timer.
            if (r.NativeSeen && _native == null || (now - r.Received).TotalSeconds > 30)
                _receipts.RemoveAt(i);
        }
    }

    internal void ObserveEffect(MechanicEventActionEffect packet, ActorCastEvent action, WorldState world)
    {
        if (!_forecasts.TryGetValue((packet.CasterId, packet.ActionId), out var forecast)) return;
        if (packet.ReceivedAt == default) return;
        var player = world.Party.Player();
        if (player == null) return;
        foreach (var target in action.Targets)
        {
            if (target.ID != _player) continue;
            foreach (ref readonly var effect in target.Effects.ValidEffects())
            {
                if (effect.Type != ActionEffectType.Knockback) continue;
                var row = Service.LuminaGameData.GetExcelSheet<Lumina.Excel.Sheets.Knockback>()?.GetRowOrDefault(effect.Value);
                if (row is not { Speed: > 0 } data) continue;
                var distance = data.Distance + effect.Param0;
                var received = packet.ReceivedAt != default ? packet.ReceivedAt : world.CurrentTime;
                var source = forecast.Source;
                var casterOid = world.Actors.Find(packet.CasterId)?.OID ?? 0;
                var timelineKey = casterOid != 0 ? KnockbackMotionData.GetTimelineKey(packet.ActionId, casterOid) : forecast.Timing.TimelineKey;
                var confirmed = forecast with
                {
                    Source = new(source.Origin, distance, received, source.Shape, source.Direction,
                        source.Kind, source.MinDistance, source.SafeWalls, source.ActorID,
                        source.IgnoreImmunes, packet.ActionId, true),
                    Timing = new(KnockbackMotionData.ApplicationDelay(timelineKey) ?? double.PositiveInfinity,
                        (double)distance / data.Speed, packet.ActionId, data.RowId, TimelineKey: timelineKey)
                };
                if (_receipts.Any(r => r.Sequence == packet.GlobalSequence && r.Prediction.Source.ActorID == packet.CasterId)) continue;
                if (_receipts.Count == 32) _receipts.RemoveAt(0);
                _receipts.Add(new(confirmed, packet.GlobalSequence, received,
                    packet.LocalPosition ?? new(player.Position.X, player.Position.Z), confirmed.Timing.Duration,
                    packet.Timeline, forecast.Source.Activation));
            }
        }
    }

    internal (KnockbackPrediction[] Forecasts, NativeDisplacementObservation? Current) Capture(
        EncounterPlanningSnapshot snapshot, KnockbackPrediction[] forecasts)
    {
        var now = snapshot.CapturedAt;
        foreach (ref readonly var forecast in forecasts.AsSpan())
            if (forecast.Source.ActionID != 0)
                _forecasts[(forecast.Source.ActorID, forecast.Source.ActionID)] = forecast;
        // The dictionary is limited to the encounter's actors and action IDs.
        // Remove old identities incrementally at receipt retirement/capture.
        if (_forecasts.Count > 128)
            foreach (var key in _forecasts.Where(p => (now - p.Value.Source.Activation).TotalSeconds > 30).Select(p => p.Key).ToArray())
                _forecasts.Remove(key);

        if (_receipts.Count == 0) return (forecasts, _native);
        NativeDisplacementObservation? current = _native;
        foreach (var receipt in _receipts)
        {
            foreach (ref readonly var clock in _timelines.AsSpan())
                if (clock.CasterId == receipt.Prediction.Source.ActorID && clock.GlobalSequence == receipt.Sequence
                    && clock.ActionId == receipt.Prediction.Source.ActionID)
                    receipt.Timeline = clock;
            if (_native is { } native && _receipts.Count == 1)
            {
                receipt.NativeSeen = true;
                current = native with { ActionId = receipt.Prediction.Source.ActionID,
                    GlobalSequence = receipt.Sequence, ReceivedAt = receipt.Received };
                continue;
            }
            if (current != null || receipt.NativeSeen || !receipt.Prediction.Timing.Available
                || snapshot.Hints?.PendingKnockback != true) continue;
            if (receipt.Timeline is not { } timeline) continue;
            if (!float.IsFinite(timeline.Speed) || timeline.Speed <= 0) continue;
            var remainingTicks = receipt.Prediction.Timing.ApplicationDelay * 30 - timeline.Timestamp;
            // The marker already fired: only the native movement record
            // can identify the resulting active motion, not another timer.
            if (remainingTicks <= 0) continue;
            var begins = timeline.ObservedAt.AddSeconds(remainingTicks / (30 * timeline.Speed));
            if (begins <= now) continue;
            var to = EncounterDisplacement.Project(snapshot, receipt.Prediction, receipt.Feet, default);
            current = new(to, now, begins, receipt.Duration, 0, receipt.Prediction.Source.Distance,
                false, receipt.Prediction.Source.ActionID, receipt.Sequence, receipt.Received);
        }
        // A received event supersedes only the same caster/action forecast.
        // It does not shift unrelated future server events.
        var remaining = new List<KnockbackPrediction>(forecasts.Length);
        foreach (var forecast in forecasts)
            if (!_receipts.Any(r => r.Prediction.Source.ActorID == forecast.Source.ActorID
                && r.Prediction.Source.ActionID == forecast.Source.ActionID
                && forecast.Source.Activation == r.ForecastAt))
                remaining.Add(forecast);
        return (remaining.ToArray(), current);
    }
}
