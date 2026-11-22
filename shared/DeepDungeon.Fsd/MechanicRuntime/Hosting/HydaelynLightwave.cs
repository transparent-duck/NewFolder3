using BossMod;
using BossMod.Components;
using BossMod.Endwalker.Trial.T02Hydaelyn;

namespace MasterPlayer.MechanicRuntime;

// Pulse positions are sampled on action effects, not on render frames. Some
// helpers are reused in a later wave after teleporting back outside the arena.
sealed class HydaelynLightwave(BossModule module) : GenericAOEs(module)
{
    private const float Front = 4.5f, HalfWidth = 8.5f, Back = 11;
    private static readonly AOEShapeRect Pulse = new(Front, HalfWidth, Back);
    private static readonly int[] LastPulse = [1, 4, 9, 18];
    private readonly Dictionary<ulong, Track> _tracks = new(4);
    private AOEInstance[] _visible = [];

    private sealed record Track(DateTime At, WPos Origin, Angle Rotation, ShapeDistance Shape,
        WDir Step, double Period, EncounterHazard[] Forecast);

    public override ReadOnlySpan<AOEInstance> ActiveAOEs(int slot, Actor actor) => _visible;

    public override void OnEventCast(Actor caster, ActorCastEvent spell)
    {
        if (spell.Action.ID != (uint)AID.RayOfLight) return;
        var now = WorldState.CurrentTime;
        var pos = caster.Position;
        var step = default(WDir);
        double period = 0;
        if (_tracks.TryGetValue(caster.InstanceID, out var last))
        {
            var dt = (now - last.At).TotalSeconds;
            var delta = pos - last.Origin;
            if (dt is > 0.25 and < 0.85 && delta.LengthSq() is > 0.25f and < 9f
                && delta.Dot(caster.Rotation.ToDirection()) > 0)
            {
                period = last.Period > 0 ? (last.Period * 2 + dt) / 3 : dt;
                step = last.Period > 0 ? (last.Step * 2 + delta) / 3 : delta;
            }
        }
        var shape = Pulse.Distance(pos, caster.Rotation);
        var forecast = period > 0 ? Forecast(caster.InstanceID, pos, now, step, period) : [];
        _tracks[caster.InstanceID] = new(now, pos, caster.Rotation, shape, step, period, forecast);
        RefreshVisible();
    }

    private static EncounterHazard[] Forecast(ulong source, WPos origin, DateTime now, WDir step, double period)
    {
        // Envelope consecutive pulses within each window. Longer windows far
        // ahead reserve time to escape without rasterizing every pulse.
        var direction = step.Normalized();
        var result = new EncounterHazard[LastPulse.Length];
        var first = 1;
        for (var i = 0; i < result.Length; ++i)
        {
            var last = LastPulse[i];
            var shape = new SDRect(origin + step * first, direction,
                Front + step.Length() * (last - first), Back, HalfWidth);
            result[i] = new(shape, now.AddSeconds(first * period - 0.1), EncounterHazardKind.Persistent, source)
                { ExpiresAt = now.AddSeconds(last * period + 0.1) };
            first = last + 1;
        }
        return result;
    }

    internal void AddForecast(List<EncounterHazard> zones, DateTime now)
    {
        foreach (var track in _tracks.Values)
        {
            if (track.Forecast.Length == 0) continue;
            for (var i = 0; i < zones.Count; ++i)
            {
                if (!ReferenceEquals(zones[i].Shape, track.Shape)) continue;
                zones.RemoveAt(i);
                foreach (var hazard in track.Forecast)
                    if (hazard.ExpiresAt >= now) zones.Add(hazard);
                break;
            }
        }
    }

    public override void Update()
    {
        var now = WorldState.CurrentTime;
        bool changed = false;
        foreach (var (id, track) in _tracks)
            if ((now - track.At).TotalSeconds > 1.1)
            {
                _tracks.Remove(id);
                changed = true;
            }
        if (changed) RefreshVisible();
    }

    public override void OnActorDestroyed(Actor actor)
    {
        if (_tracks.Remove(actor.InstanceID)) RefreshVisible();
    }

    private void RefreshVisible()
    {
        if (_visible.Length != _tracks.Count) _visible = new AOEInstance[_tracks.Count];
        var i = 0;
        foreach (var (id, track) in _tracks)
            _visible[i++] = new(Pulse, track.Origin, track.Rotation, track.At, actorID: id, shapeDistance: track.Shape);
    }
}
