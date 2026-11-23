using System.Numerics;

namespace DeepDungeon.MechanicPlanning;

// The solver sees an opaque branch state, a model and timed velocity controls.
// Branch state includes the model's event cursor and accumulated assessment.
// Copy/CopyFrom must preserve it all; Advance mutates only the supplied branch.
abstract class PlanningProblem<TState> where TState : class
{
    public abstract double StartTime { get; }
    public abstract float Speed { get; }
    public abstract bool RequiresHistory { get; }
    public abstract double[] Ends { get; }
    public int Knots => Ends.Length;
    public double End => Ends[^1];
    public abstract PlanningProblem<TState> WithResolution(double integrationStep, double layerStep = 0);
    public abstract TState CreateState();
    public abstract TState Copy(TState source);
    public abstract void CopyFrom(TState target, TState source);
    public abstract Vector2 Position(TState state);
    public abstract void Advance(TState state, Vector2 velocity, double until);
    public abstract PlanScore Score(TState state, bool terminal);
    public virtual bool CanExpand(TState state) => Score(state, false).Failures == 0;

    // Task goals rank candidates; they must not reject a safe continuation.
    public virtual bool CanHold(PlanScore score) => score.Display < 1e-9;
    public virtual bool CanReuse(ControlPlan plan, PlanScore score) => score.GeometryFailures == 0 && score.Failures == 0;

    public PlanScore Evaluate(Vector2[] controls, double[]? ends = null)
    {
        ends ??= Ends;
        var state = CreateState();
        for (var k = 0; k < ends.Length; ++k) Advance(state, controls[k], ends[k]);
        return Score(state, true);
    }

    public PlanScore EvaluateTail(Vector2[] controls, int depth, TState prefix)
    {
        var state = Copy(prefix);
        for (var k = depth; k < Ends.Length; ++k) Advance(state, controls[k], Ends[k]);
        return Score(state, true);
    }
}

// Optional model guidance for the existing state-search algorithms. A model
// used only by another optimizer or SIPP need not implement these heuristics.
interface IStateSearchGuidance<TState> where TState : class
{
    void SampleTarget(TState state, Random random, bool perturbHistory);
    double Distance(TState a, TState b);
    ulong Label(TState state);
    double Heuristic(TState state);
    ReadOnlySpan<Vector2> ControlTargets { get; }
}

// Absolute switching times are part of every result, regardless of solver.
readonly record struct ControlPlan(Vector2[] Velocities, double[] Ends, PlanScore? Assessment = null);

interface IResearchPlanner<TState> where TState : class
{
    ControlPlan Plan(PlanningProblem<TState> problem);
    int Evaluations { get; }
    double LastCost { get; }
}
interface ISearchWork { string LastWork { get; } int Transitions { get; } }
interface ITreeDiagnostics { TreeDiagnostics? SearchDiagnostics { get; } }
sealed class TreeDiagnostics
{
    public int Extensions { get; set; }
    public string Exit { get; set; } = "budget";
}
readonly record struct ExecutedControl(double from, double until, float vx, float vz);

readonly record struct PlanScore(int Failures, double Soft, double Travel, double ImpactMarginShortfall = 0, int GeometryFailures = 0, double Exposure = 0) : IComparable<PlanScore>
{
    public double FirstViolation { get; init; }
    public bool HasStackGoal { get; init; }
    public float StackDistance { get; init; }
    public double? CompletionSlack { get; init; }
    public double? MinimumChaseClearance { get; init; }
    public bool RecoveryPending { get; init; }
    public double ResidenceMarginShortfall { get; init; }
    public double Display => GeometryFailures * 1e12 + Failures * 1e7 + Soft + Travel * .01;
    public int CompareTo(PlanScore other)
    {
        var c = GeometryFailures.CompareTo(other.GeometryFailures); if (c != 0) return c;
        c = Failures.CompareTo(other.Failures); if (c != 0) return c;
        if (Failures > 0)
        {
            c = other.FirstViolation.CompareTo(FirstViolation); if (c != 0) return c;
        }
        c = RecoveryPending.CompareTo(other.RecoveryPending); if (c != 0) return c;
        c = ResidenceMarginShortfall.CompareTo(other.ResidenceMarginShortfall); if (c != 0) return c;
        if (Failures > 0 || HasStackGoal || other.HasStackGoal)
        {
            c = ImpactMarginShortfall.CompareTo(other.ImpactMarginShortfall); if (c != 0) return c;
            c = Exposure.CompareTo(other.Exposure); if (c != 0) return c;
            c = StackDistance.CompareTo(other.StackDistance); if (c != 0) return c;
        }
        if (Failures == 0 && (CompletionSlack.HasValue || other.CompletionSlack.HasValue))
        {
            c = ImpactMarginShortfall.CompareTo(other.ImpactMarginShortfall); if (c != 0) return c;
            c = (other.CompletionSlack ?? double.NegativeInfinity).CompareTo(CompletionSlack ?? double.NegativeInfinity);
            if (c != 0) return c;
        }
        if (Failures == 0)
        {
            // A plan without a measured chase margin ranks at the safety threshold.
            c = other.MinimumChaseClearance.GetValueOrDefault().CompareTo(MinimumChaseClearance.GetValueOrDefault());
            if (c != 0) return c;
        }
        c = Soft.CompareTo(other.Soft); return c != 0 ? c : Travel.CompareTo(other.Travel);
    }
}

static class ControlMath
{
    public const double Knot = .4;
    public static Vector2[] Shift(Vector2[] previous, int count, double shift)
    {
        var next = new Vector2[count];
        for (var k = 0; k < count; ++k)
        {
            var j = Math.Clamp((int)(k + shift), 0, previous.Length - 1);
            // A shifted command stays a stop or a full-speed heading.
            next[k] = previous[j];
        }
        return next;
    }
    // Project optimizer samples onto the nearest executable command: stop,
    // or a heading at normal speed. Intermediate magnitudes are not controls.
    public static Vector2 Project(Vector2 v, float speed) =>
        v.LengthSquared() <= speed * speed * .25f ? Vector2.Zero : Vector2.Normalize(v) * speed;
}
