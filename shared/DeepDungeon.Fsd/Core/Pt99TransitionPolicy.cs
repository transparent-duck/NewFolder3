namespace DeepDungeon.Fsd.Core;

/// <summary>PT99 requires a post-boss jump portal followed by the ordinary passage on its landing side.</summary>
public static class Pt99TransitionPolicy
{
    public const uint ResultPortalBaseId = 2014940;
    public const uint PassageBaseId = 2014756;

    public static bool CanApproach(uint dungeonId, int floor, bool inCombat, bool passageOpen) =>
        dungeonId == 4 && floor == 99 && !inCombat && passageOpen;

    public static bool OnPassageSide(float distanceToJumpPortal, float distanceToPassage) =>
        distanceToJumpPortal > 30f && distanceToPassage < 30f;
}
