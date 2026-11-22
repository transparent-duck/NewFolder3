using System.Numerics;

namespace DeepDungeon.MechanicPlanning;

// Feasibility reuse is independent of beam/tree selection. Preserve the exact
// old switching times; extending the horizon still requires a full recheck.
sealed class ReusePlanner<TState>(IResearchPlanner<TState> inner) : IResearchPlanner<TState>, ISearchWork, ITreeDiagnostics
    where TState : class
{
    Vector2[] _previous = [];
    double[] _ends = [];
    bool _searched;
    public double[] ControlEnds { get; private set; } = [];
    public int Evaluations { get; private set; }
    public double LastCost { get; private set; }
    public string LastWork { get; private set; } = "search";
    public TreeDiagnostics? SearchDiagnostics => _searched && inner is ITreeDiagnostics d ? d.SearchDiagnostics : null;
    public int Transitions => _searched && inner is ISearchWork work ? work.Transitions : 0;
    // The host can replace a search result with a recovery route. Retain the
    // delivered controls, including their original absolute switching times.
    public void Retain(ControlPlan plan)
    {
        _previous = (Vector2[])plan.Velocities.Clone();
        _ends = ControlEnds = (double[])plan.Ends.Clone();
    }
    public ControlPlan Plan(PlanningProblem<TState> problem)
    {
        var q = problem.WithResolution(.05); Evaluations = 0; _searched = false;
        var hold = new Vector2[q.Knots]; var holdScore = q.Evaluate(hold); Evaluations++;
        var best = new ControlPlan(hold, q.Ends, holdScore);
        var bestScore = holdScore; var bestWork = "hold";
        ControlPlan Finish(ControlPlan plan, PlanScore score, string work)
        {
            Retain(plan); LastCost = score.Display; LastWork = work;
            return new(_previous, ControlEnds, score);
        }
        if (q.CanHold(holdScore))
        {
            return Finish(best, bestScore, bestWork);
        }
        var u = new List<Vector2>(); var ends = new List<double>();
        for (var k = 0; k < _ends.Length; ++k)
        {
            if (_ends[k] <= q.StartTime + 1e-8) continue;
            u.Add(_previous[k]); ends.Add(Math.Min(_ends[k], q.End));
            if (_ends[k] >= q.End - 1e-8) break;
        }
        if (u.Count > 0)
        {
            var appended = ends[^1] < q.End - 1e-8;
            if (appended) { ends.Add(q.End); u.Add(Vector2.Zero); }
            var candidate = u.ToArray(); var times = ends.ToArray();
            for (var tail = 0; tail < (appended ? 2 : 1); ++tail)
            {
                if (tail == 1) candidate[^1] = candidate.Length > 1 ? candidate[^2] : Vector2.Zero;
                var score = q.Evaluate(candidate, times); Evaluations++;
                if (score.CompareTo(bestScore) < 0)
                {
                    best = new((Vector2[])candidate.Clone(), times, score);
                    bestScore = score; bestWork = "reuse";
                }
                if (q.CanReuse(new(candidate, times), score))
                {
                    return Finish(best, bestScore, bestWork);
                }
            }
        }
        _searched = true;
        var next = inner.Plan(q); Evaluations += inner.Evaluations;
        var nextScore = next.Assessment ?? q.Evaluate(next.Velocities, next.Ends);
        if (next.Assessment == null) ++Evaluations;
        if (nextScore.CompareTo(bestScore) <= 0)
            return Finish(next, nextScore, "search");
        return Finish(best, bestScore, "search-" + bestWork);
    }
}
