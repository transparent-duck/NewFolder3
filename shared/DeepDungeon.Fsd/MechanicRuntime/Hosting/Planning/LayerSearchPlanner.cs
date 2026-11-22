using System.Numerics;

namespace DeepDungeon.MechanicPlanning;

// Shared event layers, controls, score, transition budget and fallback. The
// switch changes frontier selection only: bounded beam or sampled best-near tree.
sealed class LayerSearchPlanner<TState>(int budget, int width, int seed, bool beam)
    : IResearchPlanner<TState>, ISearchWork
    where TState : class
{
    readonly Random _rng = new(seed);
    readonly List<TState> _pool = [];
    int _used;
    public int Evaluations { get; private set; }
    public int Transitions { get; private set; }
    public double LastCost { get; private set; }
    public string LastWork { get; private set; } = "search";
    sealed record Node(TState State, int Parent, int Depth, Vector2 U, PlanScore Rank);
    TState Rent(PlanningProblem<TState> q, TState source)
    {
        if (_used == _pool.Count) _pool.Add(q.Copy(source)); else q.CopyFrom(_pool[_used], source);
        return _pool[_used++];
    }
    public ControlPlan Plan(PlanningProblem<TState> problem)
    {
        var q = problem.WithResolution(.05, .6); var start = q.CreateState();
        var guidance = (IStateSearchGuidance<TState>)q;
        _used = 0; Evaluations = Transitions = 0; LastWork = "search";
        var best = new Vector2[q.Knots]; var bestScore = q.Evaluate(best); Evaluations++;
        if (bestScore.Display < 1e-9) { LastWork = "hold"; LastCost = bestScore.Display; return new(best, q.Ends); }
        var nodes = new List<Node> { new(Rent(q, start), -1, 0, default, default) };
        var layers = Enumerable.Range(0, q.Knots + 1).Select(_ => new List<int>()).ToArray(); layers[0].Add(0);
        var actions = new Vector2[17 + guidance.ControlTargets.Length];
        int Actions(TState s, double dt)
        {
            actions[0] = Vector2.Zero;
            for (var k = 0; k < 16; ++k)
            {
                var angle = k * MathF.PI / 8; actions[k + 1] = new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * q.Speed;
            }
            var count = 17;
            foreach (var target in guidance.ControlTargets)
                actions[count++] = ControlMath.Project((target - q.Position(s)) / (float)dt, q.Speed);
            return count;
        }
        Vector2[] Path(int n)
        {
            var u = new Vector2[q.Knots];
            for (var i = n; nodes[i].Parent >= 0; i = nodes[i].Parent) u[nodes[i].Depth - 1] = nodes[i].U;
            return u;
        }
        int Add(int parent, Vector2 control)
        {
            if (Transitions >= budget) return -1;
            var p = nodes[parent]; var s = Rent(q, p.State);
            var depth = p.Depth + 1;
            q.Advance(s, control, q.Ends[depth - 1]);
            Transitions++; Evaluations++;
            var score = q.Score(s, depth == q.Knots);
            if (score.Failures > 0) { _used--; return -1; }
            var rank = score with { Soft = score.Soft + guidance.Heuristic(s) };
            var id = nodes.Count; nodes.Add(new(s, parent, depth, control, rank));
            if (depth == q.Knots && score.CompareTo(bestScore) < 0) { best = Path(id); bestScore = score; }
            return id;
        }
        if (beam)
        {
            for (var depth = 0; depth < q.Knots && layers[depth].Count > 0; ++depth)
            {
                var next = layers[depth + 1];
                foreach (var parent in layers[depth])
                {
                    var dt = q.Ends[depth] - (nodes[parent].Depth == 0 ? q.StartTime : q.Ends[nodes[parent].Depth - 1]);
                    var count = Actions(nodes[parent].State, dt);
                    for (var k = 0; k < count; ++k) { var id = Add(parent, actions[k]); if (id >= 0) next.Add(id); }
                }
                next.Sort((a, b) => nodes[a].Rank.CompareTo(nodes[b].Rank));
                var remaining = Math.Max(1, q.Knots - depth - 1);
                var allowed = Math.Min(width, Math.Max(1, (budget - Transitions) / (remaining * actions.Length)));
                var seen = new HashSet<ulong>(); var kept = new List<int>();
                foreach (var id in next)
                    if (seen.Add(guidance.Label(nodes[id].State))) { kept.Add(id); if (kept.Count >= allowed) break; }
                layers[depth + 1] = kept;
            }
        }
        else
        {
            var deepest = 0; var sample = q.Copy(start);
            for (var attempt = 0; attempt < budget && Transitions < budget; ++attempt)
            {
                var depth = _rng.NextDouble() < .35 ? Math.Min(deepest, q.Knots - 1) : _rng.Next(Math.Min(deepest + 1, q.Knots));
                var available = layers[depth]; if (available.Count == 0) continue;
                q.CopyFrom(sample, nodes[available[_rng.Next(available.Count)]].State);
                guidance.SampleTarget(sample, _rng, perturbHistory: false);
                var parent = -1; var near = -1; var distance = double.PositiveInfinity;
                foreach (var id in available)
                {
                    var d = guidance.Distance(nodes[id].State, sample);
                    if (d < distance) { distance = d; parent = id; }
                    if (d < 3.5 * 3.5 && (near < 0 || nodes[id].Rank.CompareTo(nodes[near].Rank) < 0)) near = id;
                }
                if (near >= 0) parent = near;
                if (parent < 0) continue;
                var dt = q.Ends[depth] - (nodes[parent].Depth == 0 ? q.StartTime : q.Ends[nodes[parent].Depth - 1]); var count = Actions(nodes[parent].State, dt);
                var choice = _rng.Next(count);
                if (_rng.NextDouble() < .5)
                {
                    var minimum = double.PositiveInfinity;
                    for (var k = 0; k < count; ++k)
                    {
                        var d = Vector2.DistanceSquared(q.Position(nodes[parent].State) + actions[k] * (float)dt, q.Position(sample));
                        if (d < minimum) { minimum = d; choice = k; }
                    }
                }
                var idNew = Add(parent, actions[choice]);
                if (idNew >= 0) { layers[depth + 1].Add(idNew); deepest = Math.Max(deepest, depth + 1); }
            }
        }
        // Partial branches are never called complete solutions. Evaluate full
        // hold/constant-control tails as fallbacks if expansion misses a solution.
        foreach (var id in Enumerable.Range(0, nodes.Count).Where(i => nodes[i].Depth < q.Knots)
                     .OrderByDescending(i => nodes[i].Depth).ThenBy(i => nodes[i].Rank).Take(8))
        {
            var u = Path(id);
            for (var tail = 0; tail < 2; ++tail)
            {
                for (var k = nodes[id].Depth; k < u.Length; ++k) u[k] = tail == 0 ? Vector2.Zero : nodes[id].U;
                var score = q.Evaluate(u); Evaluations++;
                if (score.CompareTo(bestScore) < 0) { best = (Vector2[])u.Clone(); bestScore = score; }
            }
        }
        LastCost = bestScore.Display; return new(best, q.Ends);
    }
}
