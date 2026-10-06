using DeepDungeon.Fsd.Core;
using DeepDungeon.Fsd.Dalamud.GameState;
using DeepDungeon.Fsd.Dalamud.Runtime.Search;
using FFXIVClientStructs.FFXIV.Client.Game.InstanceContent;

namespace DeepDungeon.Fsd.Dalamud.Runtime.Floor;

internal sealed partial class FloorExplorationController
{
    internal unsafe bool TryFinishHarvest(InstanceContentDeepDungeon* dd)
    {
        var ctx = _ctx;
        if (ctx?.FarmingPlan?.ReusesSave != true || this.Kind != FloorRuntimeKind.Normal ||
            _phase != FloorPhase.FloorActive || _executor == null) return false;
        if (ctx.HarvestComplete || ctx.AttemptAborted) return true;
        if (ctx.FarmingPlan.Mode == FarmingMode.HoardDiscovery)
        {
            // A spawned banded coffer proves discovery. Opening it and clearing the boss are separate events.
            var evidence = this.ObjectEvidence.Current;
            if (evidence?.Available == true && BandedChestLocator.TryFindNearestToPlayer(evidence, out var found) && found.HasValue)
            {
                ctx.HoardDiscoveries++;
                ctx.HarvestComplete = true;
            }
            else if (_chatWatchers?.ChatSaysNoHoard == true)
                ctx.HarvestComplete = true;
            else if (_executor.IsHoardWorkResolved && _executor.IsComplete && !_activeWaypoint.HasValue)
            {
                // Candidate exhaustion without authoritative absence is unknown, never a confirmed empty round.
                ctx.AttemptAborted = true;
                ctx.AttemptAbortReason = "寶藏檢查沒有取得可靠結果。";
                ctx.StatusLine = "寶藏檢查沒有取得可靠結果；停止並保留準備存檔。";
            }
        }
        else
        {
            bool chestWorkComplete = _executor.IsHoardWorkResolved && _executor.PlannedRouteCount == 0 &&
                _executor.IsComplete && !_activeWaypoint.HasValue;
            bool canSkip = EntryIncenseWindowOpen() &&
                DeepDungeonFloorItemUsePolicy.CanUsePtIncense(dd->DeepDungeonBanId) &&
                !this.Survey.NaturalPoisonfruitAttempted && !this.Survey.NaturalMazerootAttemptedOrAdopted &&
                (GetStoneCountAvailableForFloorUse(1) > 0 || GetStoneCountAvailableForFloorUse(2) > 0);
            bool passageItemPending = this.ItemUse.Pending is
            { Key: { Kind: FloorItemUseKind.Stone, ItemId: 1 or 2 } };
            if (FarmingHarvestPolicy.ShouldLeave(chestWorkComplete, canSkip, passageItemPending,
                    this.FarmingPassageItemConfirmed)) ctx.HarvestComplete = true;
        }
        if (!ctx.HarvestComplete && !ctx.AttemptAborted) return false;
        CancelActiveMovement();
        this.Objectives.ClearObjectiveDecision();
        ctx.RunOptions.Update(o => o.LeaveMode = LeaveMode.Immediate);
        _status = ctx.AttemptAborted ? ctx.StatusLine : "本次採集完成；退本並驗證準備存檔。";
        return true;
    }
}
