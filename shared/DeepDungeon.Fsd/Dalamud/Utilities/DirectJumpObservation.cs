using System.Numerics;

namespace DeepDungeon.Fsd.Dalamud.Utilities;

/// <summary>Observes native jumping and the character height; collision rays may hit below dynamic platforms.</summary>
public sealed class DirectJumpObservation
{
    private float startHeight, stableHeight,peakHeight;
    private double stableSince;
    public bool TookOff { get; private set; }
    public bool Landed { get; private set; }

    public void Begin(Vector3 position, double now)
    {
        startHeight = stableHeight = peakHeight = position.Y;
        stableSince = now;
        TookOff = Landed = false;
    }

    public void Update(Vector3 position, bool airborne, double now)
    {
        if (MathF.Abs(position.Y - stableHeight) > .02f)
        {
            stableHeight = position.Y;
            stableSince = now;
        }
        // The animation flag can remain set across physical contact and the next request.
        // Require this flight's rise, so the preceding animation cannot complete a new jump.
        TookOff |= position.Y > startHeight + .3f;
        peakHeight=Math.Max(peakHeight,position.Y);
        bool groundContact=DirectMovementCollision.FloorContact(position);
        Landed = TookOff &&
            (peakHeight>startHeight+.25f&&peakHeight-position.Y>.1f&&groundContact||!airborne&&(groundContact||now-stableSince>.12));
    }
}
