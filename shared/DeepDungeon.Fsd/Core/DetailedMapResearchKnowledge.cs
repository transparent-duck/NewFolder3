namespace DeepDungeon.Fsd.Core;

public enum DetailedMapHoardPredecessorKnowledge
{
    Unknown,
    ObservedUniqueIncoming,
    CompleteChainRoot
}

public static class DetailedMapResearchKnowledge
{
    public static DetailedMapHoardPredecessorKnowledge ResolveHoardPredecessor(
        DetailedMapCatalog? catalog,
        int layoutIndex,
        int roomIndex,
        in RawWorldPosition exactHoardPosition)
    {
        if (catalog == null ||
            !catalog.TryGetRoom(layoutIndex, roomIndex, out DetailedMapCatalogRoom room))
        {
            return DetailedMapHoardPredecessorKnowledge.Unknown;
        }

        bool hoardCandidateMatched = false;
        for (int i = 0; i < room.Candidates.Length; i++)
        {
            if (RawWorldPosition.CanonicallyEquals(
                    room.Candidates[i].Position,
                    exactHoardPosition))
            {
                hoardCandidateMatched = true;
                break;
            }
        }
        if (!hoardCandidateMatched)
            return DetailedMapHoardPredecessorKnowledge.Unknown;

        int incomingCount = 0;
        for (int i = 0; i < room.Candidates.Length; i++)
        {
            DetailedMapCatalogSuccessor successor = room.Candidates[i].Successor;
            if (successor.State != DetailedMapSuccessorState.ObservedUnique ||
                successor.Target is not { } target ||
                !RawWorldPosition.CanonicallyEquals(target, exactHoardPosition))
            {
                continue;
            }

            incomingCount++;
            if (incomingCount > 1)
                return DetailedMapHoardPredecessorKnowledge.Unknown;
        }

        if (incomingCount == 1)
            return DetailedMapHoardPredecessorKnowledge.ObservedUniqueIncoming;

        DetailedMapRoomGraphPresentation presentation =
            DetailedMapRoomGraphAnalyzer.Analyze(room);
        return presentation.State == DetailedMapRoomGraphPresentationState.Complete &&
               presentation.CompleteChainOrder.Length > 0 &&
               RawWorldPosition.CanonicallyEquals(
                   presentation.CompleteChainOrder[0],
                   exactHoardPosition)
            ? DetailedMapHoardPredecessorKnowledge.CompleteChainRoot
            : DetailedMapHoardPredecessorKnowledge.Unknown;
    }
}
