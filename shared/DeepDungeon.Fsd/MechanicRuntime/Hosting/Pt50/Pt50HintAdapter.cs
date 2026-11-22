using System;
using System.Collections.Generic;
using BossMod;
using BossMod.Endwalker.DeepDungeon.PilgrimsTraverse.DD50Ogbunabali;

namespace MasterPlayer.MechanicRuntime;

/// <summary>Quicksand is a residence limit, not a permanent wall after the
/// currently observed expiry. Upstream still owns geometry and wind timing.</summary>
internal sealed class Pt50HintAdapter
{
    private const double ResidenceSeconds = 4;
    private const double ResidenceSafetyMargin = 1;
    private ShapeDistance? _shape;
    private AOEShape? _sourceShape;
    private bool _wasSafe, _wasCertainSand;
    private DateTime _entryDeadline, _lastAt;
    private WPos _lastPosition;
    private double _safeContact, _immunityContact;

    public void Reset()
    {
        _shape = null; _sourceShape = null;
        _wasSafe = _wasCertainSand = false; _entryDeadline = _lastAt = default;
        _safeContact = _immunityContact = 0;
    }

    public EncounterResidenceHazard? Adapt(BossModule module, Actor player, List<EncounterHazard> zones, DateTime now)
    {
        if (module is not DD50Ogbunabali || module.FindComponent<Quicksand>() is not { } sand)
        { Reset(); return null; }
        var aoes = sand.ActiveAOEs(PartyState.PlayerSlot, player);
        if (aoes.Length != 1) { Reset(); return null; }
        ref readonly var aoe = ref aoes[0];
        if (!ReferenceEquals(_sourceShape, aoe.Shape))
        {
            Reset();
            _sourceShape = aoe.Shape;
            var region = aoe.Shape.Distance(aoe.Origin, aoe.Rotation);
            // Upstream polygons return zero inside. Residence prediction needs
            // interior clearance too, supplied by the upstream inverse shape.
            // Cache both fields once per sand layout, including the wind hint switch.
            _shape = region is SDPolygonWithHoles
                && aoe.Shape is AOEShapeCustom { Polygon: { } polygon }
                ? new QuicksandDistance(region, aoe.Shape.InvertedDistance(aoe.Origin, aoe.Rotation), polygon, aoe.Origin)
                : region;
        }
        // Both residence and wind immunity use the branch state below.
        // Remove only this component's exact hint object.
        if (aoe.ShapeDistance is { } normal)
        {
            var match = -1;
            for (var i = 0; i < zones.Count; ++i)
                if (ReferenceEquals(zones[i].Shape, normal) && zones[i].Activation == aoe.Activation)
                { if (match >= 0) return null; match = i; }
            if (match < 0) return null;
            zones.RemoveAt(match);
        }
        var expiry = player.FindStatus(567)?.ExpireAt ?? default;
        var immune = player.FindStatus(4625) != null;
        var distance = _shape!.Distance(player.Position);
        var safe = distance >= EncounterHintPlanner.HintMargin;
        var dt = _lastAt == default ? 0 : Math.Max(0, (now - _lastAt).TotalSeconds);
        var continuous = dt < .25 && (player.Position - _lastPosition).LengthSq() <= (float)Math.Pow(6 * dt + .15, 2);
        _safeContact = safe && _wasSafe && continuous ? _safeContact + dt : 0;
        _immunityContact = distance < -EncounterHintPlanner.HintMargin && _wasCertainSand && continuous ? _immunityContact + dt : 0;
        // Status receipts establish the current episode. Geometry alone never
        // clears an observed deadline; contact timers predict candidate futures.
        if (expiry != default) _entryDeadline = expiry;
        else if (safe) _entryDeadline = DateTime.MaxValue;
        else if (_entryDeadline == default || _entryDeadline == DateTime.MaxValue || _wasSafe)
            _entryDeadline = now.AddSeconds(ResidenceSeconds);
        _lastAt = now; _lastPosition = player.Position; _wasSafe = safe;
        _wasCertainSand = distance < -EncounterHintPlanner.HintMargin;
        DateTime? windAt = aoe.Color == Colors.SafeFromAOE ? aoe.Activation : null;
        foreach (var actor in module.WorldState.Actors)
            if (actor.CastInfo is { } cast && cast.Action.ID == (uint)AID.BitingWind)
                windAt = module.CastFinishAt(cast);
        return new(_shape, _entryDeadline, ResidenceSeconds, 567)
        {
            // Existing bounded-causal research profile, separate from actual receipts.
            ResetDwellSeconds = .3, ImmunityGrantSeconds = .62, ImmunityLossSeconds = 0,
            SafetyMarginSeconds = ResidenceSafetyMargin,
            // ARR records approximately 5 m/s while sinking. Plan both sides
            // of the boundary at that speed until walking supports zone speeds.
            MaximumPlanningSpeed = 5,
            ResetBoundary = _shape as IResidenceBoundary,
            InitialSafeContact = _safeContact, InitialImmunityContact = _immunityContact,
            StatusPresent = expiry != default, InitialActive = _entryDeadline != DateTime.MaxValue,
            WindImmune = immune, ImmunityStatusId = (uint)SID.BreakerOfWind, RequiresImmunityAt = windAt,
        };
    }

    private sealed class QuicksandDistance(ShapeDistance normal, ShapeDistance inverted,
        RelSimplifiedComplexPolygon polygon, WPos origin) : ShapeDistance, IResidenceBoundary
    {
        private readonly (Vector2 A, Vector2 Delta, float InverseLengthSquared)[] _edges = BuildEdges(polygon, origin);

        public override float Distance(in WPos p) => normal.Contains(p)
            ? -inverted.Distance(p) : normal.Distance(p);
        public override bool Contains(in WPos p) => normal.Contains(p);

        public float DistanceLowerBound(Vector2 position)
        {
            if (!Contains(new(position.X, position.Y))) return 0;
            var lengthSquared = float.PositiveInfinity;
            foreach (var (a, delta, inverse) in _edges)
            {
                var offset = position - a;
                var along = Math.Clamp(Vector2.Dot(offset, delta) * inverse, 0, 1);
                lengthSquared = Math.Min(lengthSquared, Vector2.DistanceSquared(offset, delta * along));
            }
            return float.IsFinite(lengthSquared) ? MathF.Sqrt(lengthSquared) : 0;
        }

        private static (Vector2, Vector2, float)[] BuildEdges(RelSimplifiedComplexPolygon polygon, WPos origin)
        {
            var edges = new List<(Vector2, Vector2, float)>();
            foreach (var part in polygon.Parts)
            {
                Add(part.Exterior);
                for (var h = 0; h < part.HoleStarts.Count; ++h) Add(part.Interior(h));
            }
            return edges.ToArray();

            void Add(ReadOnlySpan<WDir> vertices)
            {
                for (var i = 0; i < vertices.Length; ++i)
                {
                    var a = origin + vertices[i];
                    var delta = (vertices[(i + 1) % vertices.Length] - vertices[i]).ToVec2();
                    if (delta.LengthSquared() > 0) edges.Add((a.ToVec2(), delta, 1 / delta.LengthSquared()));
                }
            }
        }
    }
}
