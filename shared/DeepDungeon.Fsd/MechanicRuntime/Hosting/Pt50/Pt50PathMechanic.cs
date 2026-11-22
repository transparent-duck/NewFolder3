using BossMod;
using DeepDungeon.MechanicPlanning;

namespace MasterPlayer.MechanicRuntime;

/// <summary>Observed status and predicted contact response are separate inputs.</summary>
public sealed partial record EncounterResidenceHazard(ShapeDistance UnsafeRegion, DateTime InitialDeadline,
    double ReentrySeconds, uint StatusId) : EncounterPathMechanic
{
    public double ResetDwellSeconds { get; init; }
    public double SafetyMarginSeconds { get; init; }
    public double ImmunityGrantSeconds { get; init; }
    public double ImmunityLossSeconds { get; init; }
    public float MaximumPlanningSpeed { get; init; } = float.PositiveInfinity;
    internal IResidenceBoundary? ResetBoundary { get; init; }
    public double InitialSafeContact { get; init; }
    public double InitialImmunityContact { get; init; }
    public bool? StatusPresent { get; init; }
    public bool? InitialActive { get; init; }
    public bool WindImmune { get; init; }
    public uint ImmunityStatusId { get; init; }
    public DateTime? RequiresImmunityAt { get; init; }
    internal double TargetContactSlack => Math.Max(0, (ReentrySeconds - SafetyMarginSeconds - ImmunityGrantSeconds - ResetDwellSeconds) / 4);

    internal override DateTime? ResolutionAt => RequiresImmunityAt;
    internal override DateTime Deadline => InitialDeadline;
    internal override bool Valid => UnsafeRegion != null && double.IsFinite(ReentrySeconds) && ReentrySeconds > 0;

    internal override double RequiredHorizon(DateTime now)
    {
        var end = 2 * ReentrySeconds;
        if (InitialDeadline != DateTime.MaxValue)
            end = Math.Max(end, (InitialDeadline - now).TotalSeconds);
        if (RequiresImmunityAt is { } wind && wind != DateTime.MaxValue)
            end = Math.Max(end, (wind - now).TotalSeconds + ReentrySeconds);
        return end;
    }

    internal override bool Initialize(ref MechanicBranchState branch, Vector2 position, DateTime now, double start)
    {
        branch.Residence = EncounterResidenceRules.Create(this, position, now, start);
        branch.WindAt = double.PositiveInfinity;
        ref var state = ref branch.Residence;
        state.Reported = state.Active && state.Deadline <= start;
        return state.Reported;
    }

    internal override MechanicTransition Advance(ref MechanicBranchState branch,
        Vector2 from, Vector2 to, double start, double end)
    {
        ref var state = ref branch.Residence;
        var wasImmune = state.Immune;
        var result = EncounterResidenceRules.Advance(ref state, this, from, to, start, end);
        if (wasImmune && !state.Immune && start >= branch.WindAt)
            branch.WindSlack = Math.Min(branch.WindSlack, end - state.ImmunityExit - branch.WindAt);
        return new(result.Hits, result.Exposure, result.FirstViolation, UnsafeRegion);
    }

    internal override bool Resolve(ref MechanicBranchState branch, double at)
    {
        branch.WindAt = at;
        branch.WindSlack = branch.Residence.ImmunityContact - ImmunityGrantSeconds;
        return branch.Residence.Immune;
    }

    internal override bool GrantsImmunity(in MechanicBranchState branch, uint status) =>
        status != 0 && status == ImmunityStatusId && branch.Residence.Immune;

    internal override void Sample(ref MechanicBranchState branch, double time, Random random) =>
        branch.Residence.Deadline = time + random.NextDouble() * ReentrySeconds;

    internal override double AddDistance(double distance, in MechanicBranchState first, in MechanicBranchState second)
    {
        ref readonly var a = ref first.Residence;
        ref readonly var b = ref second.Residence;
        if (a.Active != b.Active || a.Reported != b.Reported || a.Immune != b.Immune)
            return double.PositiveInfinity;
        if (double.IsFinite(a.Deadline) && double.IsFinite(b.Deadline))
            distance += Math.Pow((a.Deadline - b.Deadline) * 6, 2);
        return distance + (Math.Pow((a.SafeContact - b.SafeContact) * 6, 2)
            + Math.Pow((a.ImmunityContact - b.ImmunityContact) * 6, 2));
    }

    internal override double BranchDeadline(in MechanicBranchState branch) => branch.Residence.Deadline;

    internal override PlanScore ScoreTerminal(in MechanicBranchState branch, double time, PlanScore score)
    {
        ref readonly var state = ref branch.Residence;
        score = score with { ResidenceMarginShortfall = MarginShortfall(state) };
        if (state.Active)
        {
            var age = ReentrySeconds - (state.Deadline - time);
            score = score with { RecoveryPending = true, Soft = score.Soft + Math.Pow(Math.Max(0, age - 1.5), 2) * 3000 };
        }
        if (state.LastResetAt >= branch.WindAt && !state.Active && double.IsFinite(branch.WindAt))
            score = score with { CompletionSlack = Math.Min(TargetContactSlack, Math.Min(branch.WindSlack, state.MinimumResetSlack)) };
        return score;
    }

    internal double MarginShortfall(in EncounterResidenceState state) =>
        Math.Max(0, SafetyMarginSeconds - state.MinimumResetSlack);
}

// Inline storage travels with the pooled search branch. Other simultaneous
// mechanics (including chasers) advance against the same positions and clock.
internal partial struct MechanicBranchState
{
    internal EncounterResidenceState Residence;
    internal double WindAt, WindSlack;
}
