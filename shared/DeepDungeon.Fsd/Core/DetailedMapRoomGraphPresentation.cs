namespace DeepDungeon.Fsd.Core;

public enum DetailedMapRoomGraphPresentationState
{
    NoPositions,
    Candidate,
    Partial,
    Complete,
    Conflict
}

public readonly record struct DetailedMapRoomObservedEdge(
    RawWorldPosition Source,
    RawWorldPosition Target);

public sealed class DetailedMapRoomGraphPresentation
{
    public required DetailedMapRoomGraphPresentationState State { get; init; }
    public required DetailedMapRoomObservedEdge[] ObservedEdges { get; init; }
    public required RawWorldPosition[] CompleteChainOrder { get; init; }
}

public static class DetailedMapRoomGraphAnalyzer
{
    public static DetailedMapRoomGraphPresentation Analyze(
        DetailedMapCatalogRoom room)
    {
        ArgumentNullException.ThrowIfNull(room);
        ArgumentNullException.ThrowIfNull(room.Candidates);

        int candidateCount = room.Candidates.Length;
        if (candidateCount == 0)
            return Create(DetailedMapRoomGraphPresentationState.NoPositions, [], []);

        bool conflict = false;
        var incoming = new int[candidateCount];
        var outgoing = new int[candidateCount];
        Array.Fill(outgoing, -1);
        var edges = new List<DetailedMapRoomObservedEdge>(candidateCount);

        for (int sourceIndex = 0; sourceIndex < candidateCount; sourceIndex++)
        {
            DetailedMapCatalogCandidate source = room.Candidates[sourceIndex]
                ?? throw new ArgumentException("Room contains a null candidate.", nameof(room));
            for (int previousIndex = 0; previousIndex < sourceIndex; previousIndex++)
            {
                if (RawWorldPosition.CanonicallyEquals(
                        room.Candidates[previousIndex].Position,
                        source.Position))
                {
                    conflict = true;
                }
            }

            DetailedMapCatalogSuccessor successor = source.Successor;
            if (successor == null)
            {
                conflict = true;
                continue;
            }
            if (successor.State == DetailedMapSuccessorState.Conflict)
            {
                conflict = true;
                continue;
            }
            if (successor.State != DetailedMapSuccessorState.ObservedUnique)
                continue;
            if (!successor.Target.HasValue)
            {
                conflict = true;
                continue;
            }

            int targetIndex = DetailedMapRoomCandidatePlanner.FindUniqueCandidate(
                room.Candidates,
                successor.Target.Value);
            if (targetIndex < 0)
            {
                conflict = true;
                continue;
            }

            edges.Add(new DetailedMapRoomObservedEdge(
                source.Position,
                successor.Target.Value));
            outgoing[sourceIndex] = targetIndex;
            incoming[targetIndex]++;
            if (sourceIndex == targetIndex || incoming[targetIndex] > 1)
                conflict = true;
        }

        if (HasCycle(outgoing))
            conflict = true;
        if (conflict)
            return Create(DetailedMapRoomGraphPresentationState.Conflict, edges.ToArray(), []);
        if (edges.Count == 0)
            return Create(DetailedMapRoomGraphPresentationState.Candidate, [], []);

        if (edges.Count != candidateCount - 1)
            return Create(DetailedMapRoomGraphPresentationState.Partial, edges.ToArray(), []);

        int startIndex = -1;
        int endIndex = -1;
        for (int index = 0; index < candidateCount; index++)
        {
            bool isStart = incoming[index] == 0 && outgoing[index] >= 0;
            bool isEnd = incoming[index] == 1 && outgoing[index] < 0;
            bool isMiddle = incoming[index] == 1 && outgoing[index] >= 0;
            if (isStart)
            {
                if (startIndex >= 0)
                    return Create(DetailedMapRoomGraphPresentationState.Partial, edges.ToArray(), []);
                startIndex = index;
            }
            else if (isEnd)
            {
                if (endIndex >= 0)
                    return Create(DetailedMapRoomGraphPresentationState.Partial, edges.ToArray(), []);
                endIndex = index;
            }
            else if (!isMiddle)
            {
                return Create(DetailedMapRoomGraphPresentationState.Partial, edges.ToArray(), []);
            }
        }

        if (startIndex < 0 || endIndex < 0)
            return Create(DetailedMapRoomGraphPresentationState.Partial, edges.ToArray(), []);

        var order = new RawWorldPosition[candidateCount];
        int current = startIndex;
        for (int orderIndex = 0; orderIndex < candidateCount; orderIndex++)
        {
            if (current < 0)
                return Create(DetailedMapRoomGraphPresentationState.Partial, edges.ToArray(), []);
            order[orderIndex] = room.Candidates[current].Position;
            current = outgoing[current];
        }
        if (current >= 0 ||
            !RawWorldPosition.CanonicallyEquals(
                order[^1],
                room.Candidates[endIndex].Position))
        {
            return Create(DetailedMapRoomGraphPresentationState.Partial, edges.ToArray(), []);
        }

        return Create(DetailedMapRoomGraphPresentationState.Complete, edges.ToArray(), order);
    }

    private static bool HasCycle(IReadOnlyList<int> outgoing)
    {
        var state = new byte[outgoing.Count];
        for (int start = 0; start < outgoing.Count; start++)
        {
            int current = start;
            while (current >= 0 && state[current] == 0)
            {
                state[current] = 1;
                current = outgoing[current];
            }
            if (current >= 0 && state[current] == 1)
                return true;

            current = start;
            while (current >= 0 && state[current] == 1)
            {
                state[current] = 2;
                current = outgoing[current];
            }
        }
        return false;
    }

    private static DetailedMapRoomGraphPresentation Create(
        DetailedMapRoomGraphPresentationState state,
        DetailedMapRoomObservedEdge[] edges,
        RawWorldPosition[] order) =>
        new()
        {
            State = state,
            ObservedEdges = edges,
            CompleteChainOrder = order
        };
}
