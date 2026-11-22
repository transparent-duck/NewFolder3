using System.Buffers.Binary;
using BossMod;
using static BossMod.Components.GenericKnockback;

namespace MasterPlayer.MechanicRuntime;

/// <summary>Detached native-data timing. Inferred duration is distinguished from an observed effect row.</summary>
public readonly record struct KnockbackMotionTiming(double ApplicationDelay, double Duration,
    uint ActionId = 0, uint EffectRowId = 0, bool DurationEstimated = false, string? TimelineKey = null)
{
    public bool Available => double.IsFinite(ApplicationDelay) && ApplicationDelay >= 0
        && double.IsFinite(Duration) && Duration >= 0;
}

/// <summary>Game-data adapter, called while building hints, never from a planner worker.
/// TMB and sheet work is cached per action/geometry; no per-frame file reads.</summary>
internal static class KnockbackMotionData
{
    private static readonly Dictionary<string, double?> Delays = new(StringComparer.Ordinal);
    private static readonly Dictionary<(uint Action, uint CasterOid), string?> TimelineKeys = new();
    private static readonly Dictionary<(uint Action, string? TimelineKey, float Distance, Kind Kind), KnockbackMotionTiming> Profiles = new();
    private static readonly Dictionary<uint, HashSet<(ushort Row, byte Extra)>> ObservedRows = new();
    private static Lumina.Excel.Sheets.Knockback[]? _rows;

    internal static void Observe(ActorCastEvent action)
    {
        if (action.Action.Type != ActionType.Spell) return;
        foreach (var target in action.Targets)
            foreach (ref readonly var effect in target.Effects.ValidEffects())
                if (effect.Type == ActionEffectType.Knockback)
                {
                    if (!ObservedRows.TryGetValue(action.Action.ID, out var rows))
                        ObservedRows.Add(action.Action.ID, rows = new());
                    // Invalidate only this action on new evidence; keep raw row identity.
                    if (rows.Add((effect.Value, effect.Param0)))
                        foreach (var key in Profiles.Keys.Where(k => k.Action == action.Action.ID).ToArray())
                            Profiles.Remove(key);
                }
    }

    internal static KnockbackMotionTiming Resolve(uint action, uint casterOid, float distance, Kind kind, bool effectActivation)
    {
        var timelineKey = GetTimelineKey(action, casterOid);
        var key = (action, timelineKey, distance, kind);
        if (!Profiles.TryGetValue(key, out var profile))
        {
            var rows = _rows ??= Service.LuminaGameData.GetExcelSheet<Lumina.Excel.Sheets.Knockback>()!.ToArray();
            var speed = float.PositiveInfinity;
            uint rowId = 0;
            var observed = ObservedRows.TryGetValue(action, out var ids);
            var matches = 0;
            foreach (var row in rows)
            {
                if (row.Speed == 0) continue;
                var extra = distance - row.Distance;
                if (observed ? extra < 0 || extra > 255 || extra != MathF.Truncate(extra)
                        || !ids!.Contains(((ushort)row.RowId, (byte)extra))
                    : Math.Abs(row.Distance - distance) > .001f || !MatchesDirection(row.Direction, kind)) continue;
                ++matches;
                if (row.Speed < speed) { speed = row.Speed; rowId = row.RowId; }
            }
            // Without an observed row this is a data-derived estimate, not a
            // proved bound: an effect can add Param0 to a different base distance.
            // Never present a same-distance match as an identified native row.
            if (!float.IsFinite(speed))
                foreach (var row in rows)
                    if (row.Speed > 0) speed = Math.Min(speed, row.Speed);
            var delay = ApplicationDelay(timelineKey);
            profile = new(delay ?? double.PositiveInfinity,
                float.IsFinite(speed) ? Math.Max(0, (double)distance) / speed : double.PositiveInfinity,
                action, observed && matches == 1 ? rowId : 0, !observed || matches != 1, timelineKey);
            Profiles.Add(key, profile);
        }
        // Explicit movement-time forecasts already include application timing.
        return effectActivation ? profile : profile with { ApplicationDelay = 0 };
    }

    private static bool MatchesDirection(byte direction, Kind kind) => kind switch
    {
        Kind.AwayFromOrigin => direction is 0 or 6,
        Kind.DirForward => direction is 1 or 3,
        Kind.DirLeft => direction == 5,
        Kind.DirRight => direction == 4,
        _ => true,
    };

    internal static string? GetTimelineKey(uint action, uint casterOid)
    {
        var identity = (action, casterOid);
        if (TimelineKeys.TryGetValue(identity, out var key)) return key;
        var data = Service.LuminaGameData;
        key = data.GetExcelSheet<Lumina.Excel.Sheets.Action>()?.GetRowOrDefault(action)
            ?.AnimationEnd.ValueNullable?.Key.ExtractText();
        const string placeholder = "[SKL_ID]";
        if (key?.Contains(placeholder, StringComparison.Ordinal) == true && casterOid != 0)
        {
            var model = data.GetExcelSheet<Lumina.Excel.Sheets.BNpcBase>()?.GetRowOrDefault(casterOid)
                ?.ModelChara.ValueNullable;
            var prefix = model?.Type switch { 2 => "d", 3 => "m", _ => null };
            if (prefix != null && model!.Value.Model != 0)
                key = key.Replace(placeholder, FormattableString.Invariant($"{prefix}{model.Value.Model:D4}"), StringComparison.Ordinal);
        }
        TimelineKeys.Add(identity, key);
        return key;
    }

    internal static double? ApplicationDelay(string? key)
    {
        if (string.IsNullOrEmpty(key) || key.Contains("[SKL_ID]", StringComparison.Ordinal)) return null;
        if (Delays.TryGetValue(key, out var value)) return value;
        value = ReadApplicationDelay(Service.LuminaGameData.GetFile($"chara/action/{key}.tmb")?.Data);
        Delays.Add(key, value);
        return value;
    }

    // TMLB -> TMAL actors -> TMAC tracks -> TMTR events. Offsets are relative
    // to entry+8, and references are int16 ids. Conditional tracks are unknown.
    // The shared C011 timeline task consumes the incoming ActionEffect entry
    // through 141907B5E -> 140B2A730 -> 140900C90, not at network receipt.
    internal static double? ReadApplicationDelay(byte[]? data)
    {
        if (data == null || data.Length < 12 || data.Length > 16 * 1024 * 1024
            || U32(data, 0) != 0x424C4D54 || I32(data, 4) != data.Length) return null;
        var count = I32(data, 8);
        if (count is < 1 or > 16384) return null;
        var nodes = new Dictionary<short, int>();
        var roots = new List<int>();
        var next = 12;
        for (var n = 0; n < count; ++n)
        {
            var offset = next;
            if (offset < 0 || offset > data.Length - 8) return null;
            var size = I32(data, offset + 4);
            if (size < 8 || size > data.Length - offset) return null;
            next = offset + size;
            var magic = U32(data, offset);
            if (magic == 0x4C414D54) roots.Add(offset); // TMAL
            else if (magic != 0x50504D54 && size >= 12 && !nodes.TryAdd(I16(data, offset + 8), offset)) return null;
        }
        double? result = null;
        foreach (var root in roots)
        {
            var actors = References(data, root, 8);
            if (actors == null) return null;
            foreach (var actorId in actors)
            {
                if (!nodes.TryGetValue(actorId, out var actor) || U32(data, actor) != 0x43414D54
                    || I32(data, actor + 4) < 28) return null;
                var tracks = References(data, actor, 20);
                if (tracks == null) return null;
                foreach (var trackId in tracks)
                {
                    if (!nodes.TryGetValue(trackId, out var track) || U32(data, track) != 0x52544D54
                        || I32(data, track + 4) < 24
                        || I32(data, track + 20) != 0) return null;
                    var events = References(data, track, 12);
                    if (events == null) return null;
                    foreach (var eventId in events)
                    {
                        if (!nodes.TryGetValue(eventId, out var e)) return null;
                        var magic = U32(data, e);
                        if (magic != 0x31313043 || I32(data, e + 4) < 20 || I32(data, e + 12) == 0) continue;
                        var frames = I16(data, e + 10) + I16(data, track + 10) + I16(data, actor + 10);
                        if (frames < 0) return null;
                        var seconds = frames / 30d;
                        // Different application events require target/track
                        // selection that this detached forecast does not have.
                        if (result.HasValue && result.Value != seconds) return null;
                        result = seconds;
                    }
                }
            }
        }
        return result;
    }
    private static short[]? References(byte[] data, int node, int field)
    {
        if (I32(data, node + 4) < field + 8) return null;
        var relative = I32(data, node + field);
        var count = I32(data, node + field + 4);
        var offset = (long)node + 8 + relative;
        if (count is < 0 or > 16384 || offset < 0 || offset + count * 2L > data.Length) return null;
        var result = new short[count];
        for (var i = 0; i < count; i++) result[i] = I16(data, (int)offset + i * 2);
        return result;
    }
    private static short I16(byte[] d, int o) => BinaryPrimitives.ReadInt16LittleEndian(d.AsSpan(o, 2));
    private static int I32(byte[] d, int o) => BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(o, 4));
    private static uint U32(byte[] d, int o) => BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(o, 4));
}
