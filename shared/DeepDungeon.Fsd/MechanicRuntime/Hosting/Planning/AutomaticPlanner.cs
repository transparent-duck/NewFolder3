namespace DeepDungeon.MechanicPlanning;

// A single policy at the planning entry point. Both solvers return one complete
// route for all active constraints; the caller wraps this in one ReusePlanner.
sealed class AutomaticPlanner<TState>(IResearchPlanner<TState> intervals, IResearchPlanner<TState> forward,
    string method = "auto", IResearchPlanner<TState>? research = null)
    : IResearchPlanner<TState>, ISearchWork, ITreeDiagnostics where TState : class
{
    IResearchPlanner<TState>? _selected;
    public string LastSolver { get; private set; } = "none";
    public int Evaluations => _selected?.Evaluations ?? 0;
    public double LastCost => _selected?.LastCost ?? 0;
    public string LastWork => _selected is ISearchWork work ? work.LastWork : "search";
    public int Transitions => _selected is ISearchWork work ? work.Transitions : 0;
    public TreeDiagnostics? SearchDiagnostics => (_selected as ITreeDiagnostics)?.SearchDiagnostics;
    public string SelectName(bool requiresHistory) => method == "auto"
        ? requiresHistory ? "forward" : "sipp" : method;
    public ControlPlan Plan(PlanningProblem<TState> problem)
    {
        LastSolver = SelectName(problem.RequiresHistory);
        _selected = LastSolver switch
        {
            "sipp" => intervals,
            "forward" => forward,
            _ => research ?? throw new InvalidOperationException("Unknown planner: " + LastSolver),
        };
        return _selected.Plan(problem);
    }
}
