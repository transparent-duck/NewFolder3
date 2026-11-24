namespace DeepDungeon.Fsd.Core;

public enum ProgressionItemStage { Earlier, Penultimate, Final }
public enum ProgressionFloorPressure { None, Single, Severe }
public enum ProgressionItem { None, FloorClear, Serenity, Frailty, Flight, Strength, Haste }

public readonly record struct ProgressionItemStock(int Count, bool Available, bool Active = false);

public readonly record struct ProgressionItemSnapshot(
    ProgressionItemStage Stage, ProgressionFloorPressure Pressure, int RemainingMobFloors,
    bool PassageOpen, bool CurrentFloorItemUsed, bool NextFloorIsMob, bool NextFloorIsResult,
    ProgressionItemStock FloorClear, ProgressionItemStock Serenity, ProgressionItemStock Frailty,
    ProgressionItemStock Flight, ProgressionItemStock Strength, ProgressionItemStock Haste);

public readonly record struct ProgressionItemDecision(ProgressionItem Item, string Reason);

public static class ProgressionItemPolicy
{
    public const uint FrailtySlot = 11; // Heaven-on-High only.

    public static ProgressionItemStage Stage(int floor, int lastFloor) =>
        floor > lastFloor - 10 ? ProgressionItemStage.Final :
        floor > lastFloor - 20 ? ProgressionItemStage.Penultimate :
        ProgressionItemStage.Earlier;

    public static ProgressionFloorPressure Pressure(byte status, byte ban, byte danger)
    {
        bool outputStatus = status is 1 or 3 or 5;
        bool gloom = danger == 1;
        if (!outputStatus && !gloom) return ProgressionFloorPressure.None;
        return (outputStatus && gloom) || ban == DeepDungeonFloorItemUsePolicy.PomanderUseProhibitedBanId
            ? ProgressionFloorPressure.Severe : ProgressionFloorPressure.Single;
    }

    public static bool CanSpendOnCurrentFloor(bool passageOpen, bool used) => !passageOpen && !used;

    public static ProgressionItemDecision Decide(in ProgressionItemSnapshot s)
    {
        if (CanSpendOnCurrentFloor(s.PassageOpen, s.CurrentFloorItemUsed))
        {
            if (s.FloorClear.Available &&
                (s.Stage == ProgressionItemStage.Final &&
                    (s.Pressure != ProgressionFloorPressure.None || s.RemainingMobFloors <= s.FloorClear.Count) ||
                 s.Stage == ProgressionItemStage.Penultimate &&
                    (s.Pressure == ProgressionFloorPressure.Severe || s.FloorClear.Count >= 3)))
                return new(ProgressionItem.FloorClear, s.Pressure == ProgressionFloorPressure.Severe
                    ? "clear-severe-effects" : s.Pressure != ProgressionFloorPressure.None && s.Stage == ProgressionItemStage.Final
                        ? "clear-heavy-effect" : s.Stage == ProgressionItemStage.Final
                            ? "clear-stock-covers-remaining-floors" : "clear-stock-above-reserve");

            if (s.Serenity.Available)
            {
                if (s.Pressure == ProgressionFloorPressure.Severe &&
                    (s.Stage != ProgressionItemStage.Earlier || s.Serenity.Count >= 2))
                    return new(ProgressionItem.Serenity, "remove-severe-effects");
                if (s.Pressure == ProgressionFloorPressure.Single && s.Stage != ProgressionItemStage.Earlier)
                {
                    if (s.Serenity.Count >= 3)
                        return new(ProgressionItem.Serenity, "remove-heavy-effect-at-full-stock");
                    if (s.Stage == ProgressionItemStage.Final && s.RemainingMobFloors <= s.Serenity.Count)
                        return new(ProgressionItem.Serenity, "remove-heavy-effect-stock-covers-remaining-floors");
                }
            }

            if (s.Frailty is { Available: true, Active: false } &&
                (s.Stage == ProgressionItemStage.Final &&
                    (s.Pressure != ProgressionFloorPressure.None || s.RemainingMobFloors <= s.Frailty.Count) ||
                 s.Stage == ProgressionItemStage.Penultimate &&
                    (s.Frailty.Count >= 3 || s.Pressure != ProgressionFloorPressure.None && s.Frailty.Count >= 2)))
                return new(ProgressionItem.Frailty, s.Pressure == ProgressionFloorPressure.None
                    ? "weaken-enemies-stock-above-reserve" : "weaken-enemies-under-pressure");
        }

        if (s.NextFloorIsMob && s.Flight is { Available: true, Active: false } &&
            (s.Stage == ProgressionItemStage.Final ||
             s.Stage == ProgressionItemStage.Penultimate && s.Flight.Count >= 3))
            return new(ProgressionItem.Flight, s.Stage == ProgressionItemStage.Final
                ? "reduce-next-final-set-floor" : "reduce-next-floor-keep-two");

        if (s.Stage != ProgressionItemStage.Earlier && !(s.PassageOpen && s.NextFloorIsResult))
        {
            if (s.Strength is { Available: true, Active: false })
                return new(ProgressionItem.Strength, "endgame-strength-missing");
            if (s.Haste is { Available: true, Active: false })
                return new(ProgressionItem.Haste, "endgame-haste-missing");
        }

        if (s.PassageOpen) return new(ProgressionItem.None, "passage-open");
        if (s.CurrentFloorItemUsed) return new(ProgressionItem.None, "current-floor-item-already-used");
        if (s.Pressure == ProgressionFloorPressure.None)
            return new(ProgressionItem.None, "no-heavy-effect-or-spend-threshold");
        return new(ProgressionItem.None, s.Serenity.Available || s.FloorClear.Available || s.Frailty.Available
            ? "reserve-current-floor-items" : "no-usable-current-floor-item");
    }
}
