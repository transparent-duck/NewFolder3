using System;
using System.Collections.Generic;
using BossMod;
using BossMod.Endwalker.DeepDungeon.PilgrimsTraverse.DD20ForgivenEmulation;

namespace MasterPlayer.MechanicRuntime;

/// <summary>Before the star activates, forbid its smaller safe pockets.</summary>
internal sealed class Pt20HintAdapter
{
    private const float Step = .5f;
    private ArenaBounds? _arena;
    private WPos _center;
    private ShapeDistance[] _shapes = [];
    private SmallRegions? _smallRegions;

    public void Adapt(BossModule module, Actor player, List<EncounterHazard> zones, DateTime now)
    {
        if (module is not DD20ForgivenEmulation || module.FindComponent<WoodsEmbrace>() is not { } roots)
        {
            _arena = null;
            return;
        }
        var aoes = roots.ActiveAOEs(PartyState.PlayerSlot, player);
        if (aoes.IsEmpty) { _arena = null; return; }
        foreach (ref readonly var aoe in aoes)
            if (aoe.Activation <= now) return;

        var same = ReferenceEquals(_arena, module.Bounds) && _center == module.Center && _shapes.Length == aoes.Length;
        for (var i = 0; same && i < aoes.Length; ++i)
            same = ReferenceEquals(_shapes[i], aoes[i].ShapeDistance);
        if (!same)
        {
            _arena = module.Bounds;
            _center = module.Center;
            _shapes = new ShapeDistance[aoes.Length];
            for (var i = 0; i < aoes.Length; ++i)
                _shapes[i] = aoes[i].ShapeDistance ?? aoes[i].Shape.Distance(aoes[i].Origin, aoes[i].Rotation);
            _smallRegions = BuildRegions(module);
        }
        if (_smallRegions != null)
            zones.Add(new(_smallRegions, default, EncounterHazardKind.Persistent));
    }

    private SmallRegions? BuildRegions(BossModule module)
    {
        var width = (int)MathF.Ceiling(module.Bounds.Radius * 2 / Step) + 3;
        var x0 = module.Center.X - (width - 1) * Step * .5f;
        var z0 = module.Center.Z - (width - 1) * Step * .5f;
        var count = width * width;
        var labels = new int[count];
        var sizes = new int[count + 1];
        var queue = new int[count];
        for (var i = 0; i < count; ++i)
        {
            var p = new WPos(x0 + i % width * Step, z0 + i / width * Step);
            if (!module.Bounds.Contains(p - module.Center)) { labels[i] = -1; continue; }
            foreach (var shape in _shapes)
                if (shape.Distance(p) < EncounterHintPlanner.HintMargin) { labels[i] = -1; break; }
        }
        var region = 0;
        var largest = 0;
        for (var seed = 0; seed < count; ++seed)
        {
            if (labels[seed] != 0) continue;
            ++region;
            var head = 0;
            var tail = 1;
            queue[0] = seed;
            labels[seed] = region;
            while (head < tail)
            {
                var i = queue[head++];
                Visit(i % width > 0 ? i - 1 : -1);
                Visit(i % width + 1 < width ? i + 1 : -1);
                Visit(i >= width ? i - width : -1);
                Visit(i + width < count ? i + width : -1);
                void Visit(int j)
                {
                    if (j < 0 || labels[j] != 0) return;
                    labels[j] = region;
                    queue[tail++] = j;
                }
            }
            sizes[region] = tail;
            largest = Math.Max(largest, tail);
        }
        if (region < 2) return null;
        var forbidden = new bool[count];
        for (var i = 0; i < count; ++i)
            forbidden[i] = labels[i] > 0 && sizes[labels[i]] < largest;
        return new(x0, z0, width, forbidden);
    }

    // A navigation classification, not a physical distance field. The common
    // evaluator samples it; circle/donut distance shortcuts do not apply.
    private sealed class SmallRegions(float x0, float z0, int width, bool[] forbidden) : ShapeDistance
    {
        public override float Distance(in WPos p)
        {
            var x = (int)MathF.Round((p.X - x0) / Step);
            var z = (int)MathF.Round((p.Z - z0) / Step);
            return (uint)x < width && (uint)z < width && forbidden[z * width + x] ? -1f : 1f;
        }
    }
}
