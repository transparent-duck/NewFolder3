using System.Collections.Generic;
using System.Runtime.CompilerServices;
using BossMod;
using BossMod.Endwalker.DeepDungeon.PilgrimsTraverse.DD10OrnamentalLeafman;

namespace MasterPlayer.MechanicRuntime;

/// <summary>Correct Leafmash navigation geometry without changing imported hints.</summary>
internal static class Pt10HintAdapter
{
    private static readonly AOEShapeCircle LeafmashCircle = new(15f);
    private static readonly ConditionalWeakTable<ShapeDistance, ShapeDistance> CorrectedShapes = new();

    public static void Adapt(BossModule module, Actor player, List<EncounterHazard> zones)
    {
        if (module is not DD10OrnamentalLeafman || module.FindComponent<Leafmash>() is not { } leafmash)
            return;

        // Match the component's exact shape object, not every 14m circle:
        // HedgeMazing also uses 14m and must retain its original geometry.
        foreach (ref readonly var aoe in leafmash.ActiveAOEs(PartyState.PlayerSlot, player))
        {
            if (!aoe.Risky || aoe.Shape is not AOEShapeCircle { Radius: 14f }
                || aoe.ShapeDistance is not { } original)
                continue;
            for (var i = 0; i < zones.Count; ++i)
            {
                var hazard = zones[i];
                if (!ReferenceEquals(hazard.Shape, original)) continue;
                if (!CorrectedShapes.TryGetValue(original, out var corrected))
                {
                    corrected = LeafmashCircle.Distance(aoe.Origin, aoe.Rotation);
                    CorrectedShapes.Add(original, corrected);
                }
                zones[i] = hazard with
                {
                    Shape = corrected,
                    Geometry = new("Circle", aoe.Origin.X, aoe.Origin.Z, OuterRadius: 15f)
                };
            }
        }
    }
}
