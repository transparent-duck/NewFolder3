using System.Numerics;

namespace DeepDungeon.MechanicPlanning;

// Population-based shooting MPC. All samples carry their own full hybrid state.
// Elite memory + temporally correlated mutations are inspired by iCEM; this is
// an elite evolutionary variant, not an implementation of the paper's exact CEM.
sealed class ShootingPlanner<TState>(int population, int iterations, int seed,
    Func<PlanningProblem<TState>, IEnumerable<Vector2[]>> initialCandidates, bool evolve = true) : IResearchPlanner<TState>
    where TState : class
{
    const double Knot = ControlMath.Knot;
    readonly Random _rng = new(seed);
    Vector2[]? _previous;
    double _previousAt;
    public int Evaluations { get; private set; }
    public double LastCost { get; private set; }
    public ControlPlan Plan(PlanningProblem<TState> problem)
    {
        Evaluations = 0;
        var q = problem.WithResolution(.1); var knots = q.Knots;
        var pool = new List<(Vector2[] U, int Failures, double Soft, double Travel, double Cost)>();
        static int Compare((Vector2[] U, int Failures, double Soft, double Travel, double Cost) a,
            (Vector2[] U, int Failures, double Soft, double Travel, double Cost) b)
        {
            var c = a.Failures.CompareTo(b.Failures); if (c != 0) return c;
            c = a.Soft.CompareTo(b.Soft); return c != 0 ? c : a.Travel.CompareTo(b.Travel);
        }
        ControlPlan Finish()
        {
            pool.Sort(Compare); _previous = pool[0].U; _previousAt = q.StartTime; LastCost = pool[0].Cost;
            return new(pool[0].U, q.Ends);
        }
        void Add(Vector2[] u)
        {
            for (var k = 0; k < u.Length; ++k) u[k] = ControlMath.Project(u[k], q.Speed);
            var score = q.Evaluate(u);
            pool.Add((u, score.Failures, score.Soft, score.Travel, score.Display)); Evaluations++;
        }
        if (_previous != null)
        {
            var shift = (q.StartTime - _previousAt) / Knot;
            var warm = ControlMath.Shift(_previous, knots, shift);
            Add(warm);
        }
        Add(new Vector2[knots]);
        // All objective terms are nonnegative. A safe stationary trajectory
        // with no clearance/residence penalty attains the actual lower bound.
        // Avoid spending a sampling population on an already optimal request.
        if (pool.Any(x => x.Cost <= 1e-9)) return Finish();
        foreach (var candidate in initialCandidates(q)) Add(candidate);
        // This baseline bank supplies initial modes only. Mutations below can
        // change every control knot; the optimized family is not limited to it.
        pool.Sort(Compare);
        for (var iter = 0; evolve && iter < iterations; iter++)
        {
            var elite = pool.Take(12).ToArray();
            pool.Clear(); pool.AddRange(elite);
            var sigma = .7f * MathF.Pow(.6f, iter);
            for (var n = elite.Length; n < population; n++)
            {
                var parent = elite[n % elite.Length].U;
                var u = new Vector2[knots];
                float noise = Normal(), speedNoise = Normal();
                var begin = _rng.Next(knots); var length = _rng.Next(1, knots + 1);
                for (var k = 0; k < knots; k++)
                {
                    noise = .85f * noise + .527f * Normal();
                    speedNoise = .8f * speedNoise + .6f * Normal();
                    if (n % 3 == 0 && (k < begin || k >= begin + length)) { u[k] = parent[k]; continue; }
                    var theta = MathF.Atan2(parent[k].Y, parent[k].X) + sigma * noise;
                    var speed = parent[k].Length() + speedNoise * 1.2f < q.Speed * .5f ? 0 : q.Speed;
                    u[k] = new Vector2(MathF.Cos(theta), MathF.Sin(theta)) * speed;
                }
                Add(u);
            }
            pool.Sort(Compare);
        }
        return Finish();
    }

    float Normal() => (float)(Math.Sqrt(-2 * Math.Log(Math.Max(1e-12, _rng.NextDouble()))) * Math.Cos(2 * Math.PI * _rng.NextDouble()));
}
