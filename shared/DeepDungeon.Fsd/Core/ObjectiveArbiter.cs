namespace DeepDungeon.Fsd.Core
{
    public enum FloorObjectiveKind
    {
        None,
        DefeatBoss,
        OpenVisibleBandedChest,
        CompleteKnownHoard,
        DiscoverHoard,
        ActivatePassage,
        OpenPlannedChest,
        FinishCombatBeforePassage,
        EnterPassage
    }

    public enum CommandChannelPermission
    {
        Blocked,
        PrimaryObjective,
        CompatibleSidecar
    }

    public readonly record struct ObjectiveArbiterSnapshot(
        bool BossObjective,
        bool VisibleBandedChest,
        bool KnownOrConfirmedHoard,
        bool RequiredHoardDiscovery,
        bool MandatoryHoardTerminal,
        bool PassageOpen,
        bool PassageActivationRequired,
        bool CombatInProgress,
        bool RoutineCombatAllowed,
        bool ActiveChestInteraction);

    public readonly record struct ObjectiveChannelPermissions(
        CommandChannelPermission Movement,
        CommandChannelPermission Combat,
        CommandChannelPermission Interaction,
        CommandChannelPermission Transition);

    public readonly record struct ObjectiveArbiterDecision(
        FloorObjectiveKind PrimaryObjective,
        ObjectiveChannelPermissions Channels);

    public static class ObjectiveArbiter
    {
        public static ObjectiveArbiterDecision Decide(in ObjectiveArbiterSnapshot snapshot)
        {
            var primary = SelectPrimary(snapshot);
            bool enteringPassage = primary == FloorObjectiveKind.EnterPassage;
            bool finishingCombatBeforePassage = primary == FloorObjectiveKind.FinishCombatBeforePassage;
            var interaction = snapshot.ActiveChestInteraction &&
                primary == FloorObjectiveKind.OpenVisibleBandedChest
                ? CommandChannelPermission.PrimaryObjective
                : snapshot.ActiveChestInteraction && !finishingCombatBeforePassage && !enteringPassage
                    ? CommandChannelPermission.CompatibleSidecar
                    : CommandChannelPermission.Blocked;
            var channels = new ObjectiveChannelPermissions(
                primary == FloorObjectiveKind.None
                    ? CommandChannelPermission.Blocked
                    : CommandChannelPermission.PrimaryObjective,
                primary is FloorObjectiveKind.ActivatePassage or FloorObjectiveKind.DefeatBoss or FloorObjectiveKind.FinishCombatBeforePassage
                    ? CommandChannelPermission.PrimaryObjective
                    : snapshot.RoutineCombatAllowed && !enteringPassage
                        ? CommandChannelPermission.CompatibleSidecar
                        : CommandChannelPermission.Blocked,
                interaction,
                enteringPassage && snapshot.MandatoryHoardTerminal
                    ? CommandChannelPermission.PrimaryObjective
                    : CommandChannelPermission.Blocked);

            return new ObjectiveArbiterDecision(
                primary,
                channels);
        }

        private static FloorObjectiveKind SelectPrimary(in ObjectiveArbiterSnapshot snapshot)
        {
            if (snapshot.BossObjective)
                return FloorObjectiveKind.DefeatBoss;
            if (snapshot.VisibleBandedChest)
                return FloorObjectiveKind.OpenVisibleBandedChest;
            if (!snapshot.MandatoryHoardTerminal && snapshot.KnownOrConfirmedHoard)
                return FloorObjectiveKind.CompleteKnownHoard;
            if (!snapshot.MandatoryHoardTerminal && snapshot.RequiredHoardDiscovery)
                return FloorObjectiveKind.DiscoverHoard;
            if (!snapshot.PassageOpen && snapshot.PassageActivationRequired)
                return FloorObjectiveKind.ActivatePassage;
            if (snapshot.PassageOpen && snapshot.MandatoryHoardTerminal && snapshot.CombatInProgress)
                return FloorObjectiveKind.FinishCombatBeforePassage;
            if (snapshot.PassageOpen && snapshot.MandatoryHoardTerminal)
                return FloorObjectiveKind.EnterPassage;
            return FloorObjectiveKind.None;
        }
    }
}
