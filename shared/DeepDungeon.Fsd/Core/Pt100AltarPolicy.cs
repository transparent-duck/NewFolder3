namespace DeepDungeon.Fsd.Core;

/// <summary>Completion requires the interacted altar's state transition and a fresh native duty-completed event.</summary>
public static class Pt100AltarPolicy
{
    public const uint AltarBaseId = 2014754;

    public static bool CanConfirm(ulong interactedId, ulong observedId, bool targetable,
        long completionBeforeInteraction, long completionNow, bool failed) =>
        interactedId != 0 && interactedId == observedId && !targetable &&
        completionNow > completionBeforeInteraction && !failed;
}
