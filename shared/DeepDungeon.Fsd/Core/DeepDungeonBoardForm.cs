namespace DeepDungeon.Fsd.Core;

public enum DeepDungeonBoardForm
{
    Unknown,
    TwentyOnePosition,
    FifteenPosition
}

/// <summary>
/// Resolves the minimap's structural board mask from the complete room-center
/// shape. Revealed-room state is intentionally not part of this decision.
/// </summary>
public static class DeepDungeonBoardFormResolver
{
    private static readonly int[] TwentyOneBrownIndices = [0, 4, 20, 24];
    private static readonly int[] FifteenBrownIndices =
        [0, 1, 2, 3, 4, 20, 21, 22, 23, 24];

    public static bool TryResolve(
        IReadOnlyList<bool> structuralRoomCenters,
        out DeepDungeonBoardForm form)
    {
        form = DeepDungeonBoardForm.Unknown;
        if (structuralRoomCenters.Count != 25)
            return false;

        if (MatchesMask(structuralRoomCenters, TwentyOneBrownIndices))
        {
            form = DeepDungeonBoardForm.TwentyOnePosition;
            return true;
        }
        if (MatchesMask(structuralRoomCenters, FifteenBrownIndices))
        {
            form = DeepDungeonBoardForm.FifteenPosition;
            return true;
        }
        return false;
    }

    public static bool IsBrownStructuralCell(
        DeepDungeonBoardForm form,
        int index) =>
        form switch
        {
            DeepDungeonBoardForm.TwentyOnePosition =>
                index is 0 or 4 or 20 or 24,
            DeepDungeonBoardForm.FifteenPosition =>
                index <= 4 || index >= 20,
            _ => false
        };

    private static bool MatchesMask(
        IReadOnlyList<bool> centers,
        IReadOnlyList<int> brownIndices)
    {
        for (int index = 0; index < centers.Count; index++)
        {
            bool expectedCenter = !brownIndices.Contains(index);
            if (centers[index] != expectedCenter)
                return false;
        }
        return true;
    }
}
