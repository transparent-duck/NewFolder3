using global::Dalamud.Game.ClientState.Conditions;

namespace DeepDungeon.Fsd.Dalamud.Runtime.Helpers
{
    internal static class DutyTransitionUtil
    {
        public static bool IsBetweenAreas()
        {
            try
            {
                return Service.Condition[ConditionFlag.BetweenAreas] ||
                       Service.Condition[ConditionFlag.BetweenAreas51];
            }
            catch
            {
                return true;
            }
        }
    }
}

