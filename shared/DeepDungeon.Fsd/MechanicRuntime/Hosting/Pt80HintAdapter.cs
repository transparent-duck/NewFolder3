using System;
using BossMod;
using BossMod.Endwalker.DeepDungeon.PilgrimsTraverse.DD80ForgivenProfanity;

namespace MasterPlayer.MechanicRuntime;

/// <summary>locally maintained execution policy; leaves imported module and hints unchanged.</summary>
internal static class Pt80HintAdapter
{
    public static EncounterHintState Adapt(BossModule module, Actor player, EncounterHintState state, DateTime now)
    {
        // The upstream module intends to stop at six stacks, but writes null
        // to ForcedMovement, which is also the default (no override).
        if (module is DD80ForgivenProfanity
            && player.FindStatus(SID.NowhereToRun) is { Extra: >= 6 })
            return state with { Mode = AIHints.SpecialMode.NoMovement, ModeActivation = now, ModeFinish = default };
        return state;
    }
}
