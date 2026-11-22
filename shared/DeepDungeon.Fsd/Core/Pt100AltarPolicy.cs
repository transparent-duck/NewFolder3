namespace DeepDungeon.Fsd.Core;

/// <summary>Completion requires the interacted altar's state transition and a fresh native duty-completed event.</summary>
public static class Pt100AltarPolicy
{
    public const uint AltarBaseId = 2014754;

    public static bool CanConfirm(ulong interactedId, ulong observedId, bool targetable,
        long completionBeforeInteraction, long completionNow, bool failed) =>
        ResultAltarPolicy.CanConfirm(new(AltarBaseId, ResultAltarEvidence.FreshDutyCompletion),
            interactedId, observedId, targetable, completionBeforeInteraction, completionNow, 0, 0, failed);
}
