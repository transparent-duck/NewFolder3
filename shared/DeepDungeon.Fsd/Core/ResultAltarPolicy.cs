namespace DeepDungeon.Fsd.Core;

public enum ResultAltarEvidence { FreshDutyCompletion, NewExit }
public readonly record struct ResultAltarProfile(uint BaseId, ResultAltarEvidence Evidence);

/// <summary>Only live-observed result altars have an automatic completion contract.</summary>
public static class ResultAltarPolicy
{
    public const uint HohAltarBaseId = 2009505;
    public const uint ExitBaseId = 2005809;

    public static bool TryGet(uint dungeonId, int floor, out ResultAltarProfile profile)
    {
        profile = (dungeonId, floor) switch
        {
            (2, 100) => new(HohAltarBaseId, ResultAltarEvidence.NewExit),
            (4, 100) => new(Pt100AltarPolicy.AltarBaseId, ResultAltarEvidence.FreshDutyCompletion),
            _ => default
        };
        return profile.BaseId != 0;
    }

    public static bool CanConfirm(in ResultAltarProfile profile, ulong interactedId, ulong observedId,
        bool targetable, long completionBefore, long completionNow, ulong exitBefore, ulong targetableExitNow, bool failed)
    {
        if (interactedId == 0 || interactedId != observedId || targetable || failed) return false;
        return profile.Evidence switch
        {
            ResultAltarEvidence.FreshDutyCompletion => completionNow > completionBefore,
            ResultAltarEvidence.NewExit => targetableExitNow != 0 && targetableExitNow != exitBefore,
            _ => false
        };
    }
}
