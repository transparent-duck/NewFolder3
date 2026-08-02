using Lumina.Excel.Sheets;

namespace DeepDungeon.Fsd.Dalamud;

internal readonly record struct FsdSkillInfo(string Name, float Range, float EffectRange, bool IsValid);

internal static class FsdSkillCatalog
{
    private static readonly Dictionary<uint, FsdSkillInfo> Cache = new();

    public static FsdSkillInfo GetOrRegister(uint actionId)
    {
        if (Cache.TryGetValue(actionId, out var cached))
            return cached;
        var row = Service.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Action>()?.GetRow(actionId);
        if (row == null)
            return default;
        var classJob = row.Value.ClassJob.ValueNullable;
        var info = new FsdSkillInfo(
            row.Value.Name.ToString(),
            NormalizeRange(
                row.Value.Range,
                classJob.HasValue ? classJob.Value.Role : null),
            row.Value.EffectRange,
            true);
        Cache[actionId] = info;
        return info;
    }

    internal static float NormalizeRange(float rawRange, int? classJobRole)
    {
        if (rawRange >= 0f)
            return rawRange;

        // ClassJob roles: 1 tank, 2 melee, 3 physical/magical ranged, 4 healer.
        // Match the established host skill-catalog behavior without taking a host dependency.
        return classJobRole is 3 or 4 ? 25f : 3f;
    }
}
