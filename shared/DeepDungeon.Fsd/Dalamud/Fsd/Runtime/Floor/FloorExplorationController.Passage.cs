using DeepDungeon.Fsd.Core;

namespace DeepDungeon.Fsd.Dalamud.Runtime.Floor;

internal sealed partial class FloorExplorationController
{
    internal void ObserveFalsePassage()
    {
        if (IsDisposed || DungeonId != 2 || _ctx?.Duty.IsTransitioning != false ||
            _ctx.Duty.Floor != Floor) return;
        // Message 9217 is an observation edge, not a floor-local identity blacklist.
        // Eligibility always comes from the next native object-state observation.
        ObjectEvidence.Invalidate();
        if (ObjectEvidence.Current is not { Available: true } evidence ||
            Service.LocalPlayer is not { } player) return;
        ulong actorId = 0;
        var triggerRange = new NavigationRange(1.5f);
        foreach (var actor in evidence.PassageActors)
        {
            if (!triggerRange.Contains(player.Position, actor.Position)) continue;
            // The native message names no actor. Associate only an unambiguous
            // nearby trigger; do not reject an unrelated committed destination.
            if (actorId != 0) return;
            actorId = actor.GameObjectId;
        }
        RecordReplayEvent("false-passage-observed", new { floor = Floor, actorId, logMessageId = 9217, player.Position,
            sceneObservation = DeepDungeon.Fsd.Dalamud.GameState.DeepDungeonSceneObservation.CaptureCurrent() });
    }
}
