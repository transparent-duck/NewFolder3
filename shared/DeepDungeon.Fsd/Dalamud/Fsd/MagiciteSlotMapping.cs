namespace DeepDungeon.Fsd.Dalamud;

internal enum MagiciteRowKind
{
    Unknown,
    MagicStone,
    Demiclone
}

internal static class MagiciteSlotMapping
{
    internal static bool TryGetDefinitionIndex(
        byte runtimeTypeId,
        int definitionCount,
        out int definitionIndex)
    {
        definitionIndex = -1;
        if (runtimeTypeId == 0 || definitionCount <= 0)
            return false;

        int candidate = runtimeTypeId - 1;
        if ((uint)candidate >= (uint)definitionCount)
            return false;

        definitionIndex = candidate;
        return true;
    }

    internal static bool TryGetRowKind(
        byte deepDungeonType,
        int definitionIndex,
        out MagiciteRowKind rowKind)
    {
        rowKind = MagiciteRowKind.Unknown;
        if (definitionIndex < 0)
            return false;

        rowKind = deepDungeonType switch
        {
            // Current DeepDungeon sheet layout: four MagicStone definitions.
            1 when definitionIndex <= 3 => MagiciteRowKind.MagicStone,

            // Current DeepDungeon sheet layout: four Demiclone definitions.
            2 when definitionIndex <= 3 => MagiciteRowKind.Demiclone,

            // Pilgrim's Traverse mixes the two union arms by definition index.
            3 when definitionIndex == 0 => MagiciteRowKind.MagicStone,
            3 when definitionIndex is 1 or 2 => MagiciteRowKind.Demiclone,
            _ => MagiciteRowKind.Unknown
        };

        return rowKind != MagiciteRowKind.Unknown;
    }
}
