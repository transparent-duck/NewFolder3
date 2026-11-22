using System;
using System.Collections.Generic;
using System.Numerics;

namespace DeepDungeon.Fsd.Dalamud.Coord;

/// <summary>
/// Single owner of movement claims: pure arbitration only. Each tick it
/// resolves the highest-priority claim and tracks ownership transitions —
/// dispatch is the pipeline's job (boundary.Execute), so the arbiter never
/// inspects what a claim does. Safety constraints are applied by the navigation surface.
/// </summary>
internal sealed class MovementArbiter
{
    private FloorOperation? _owner;

    /// <summary>Returns the winning op; null when no claim outranks None.</summary>
    internal FloorOperation? Resolve(IReadOnlyList<(FloorOperation op, MoveClaim claim)> claims)
    {
        FloorOperation? winner = null;
        var best = MoveClaim.None;
        for (int i = 0; i < claims.Count; ++i)
        {
            var (op, claim) = claims[i];
            if (claim.Priority < best.Priority)
            {
                best = claim;
                winner = op;
            }
        }

        if (winner != _owner)
        {
            _owner?.OnPreempted();
            _owner = winner;
        }
        return winner;
    }
}
