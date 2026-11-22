using System.Numerics;

namespace DeepDungeon.MechanicPlanning;

// Algorithm-1 iCEM-style control optimizer: diagonal Gaussian refitting,
// spectral colored noise, 30% elite retention/shift, population decay,
// final mean evaluation and best evaluated elite execution. No route templates.
// Adaptations: variable announced-event horizon, 0.4s controls / 0.2s replan,
// projection onto stop/full-speed headings, and the common safety score.
sealed class ICemPlanner<TState>(int population, int iterations, int seed) : IResearchPlanner<TState>
    where TState : class
{
    readonly Random _rng = new(seed);
    Vector2[]? _mean;
    Vector2[][] _elites = [];
    double _at;
    int _basisSize;
    double[,] _cos = new double[0, 0], _sin = new double[0, 0];
    double _normalization;
    public int Evaluations { get; private set; }
    public double LastCost { get; private set; }
    readonly record struct Candidate(Vector2[] U, PlanScore Score);

    public ControlPlan Plan(PlanningProblem<TState> problem)
    {
        var q = problem.WithResolution(.1); Evaluations = 0;
        EnsureBasis(q.Knots);
        var shift = (q.StartTime - _at) / ControlMath.Knot;
        var mean = _mean == null ? new Vector2[q.Knots] : ControlMath.Shift(_mean, q.Knots, shift);
        var std = Enumerable.Repeat(new Vector2(.5f), q.Knots).ToArray();
        var elites = new List<Candidate>();
        var kElite = Math.Min(10, Math.Max(2, population / 4));
        var reused = Math.Max(1, (int)(kElite * .3));
        var pool = new List<Candidate>();
        Candidate Evaluate(Vector2[] normalized)
        {
            for (var k = 0; k < normalized.Length; ++k)
                normalized[k] = ControlMath.Project(normalized[k], 1);
            var velocity = normalized.Select(v => v * q.Speed).ToArray();
            var score = q.Evaluate(velocity); Evaluations++; return new(normalized, score);
        }
        // Common lower-bound shortcut, not a tactical/map-specific seed.
        var hold = Evaluate(new Vector2[q.Knots]);
        if (hold.Score.Display <= 1e-9)
        {
            _mean = hold.U; _elites = [hold.U]; _at = q.StartTime; LastCost = 0;
            return new(new Vector2[q.Knots], q.Ends);
        }
        for (var iteration = 0; iteration < Math.Max(1, iterations); ++iteration)
        {
            pool.Clear();
            var samples = Math.Max(2 * kElite, (int)(population * Math.Pow(1.25, -iteration)));
            for (var n = 0; n < samples; ++n)
            {
                var nx = Colored(q.Knots); var nz = Colored(q.Knots); var u = new Vector2[q.Knots];
                for (var k = 0; k < u.Length; ++k)
                    u[k] = ControlMath.Project(Vector2.Clamp(mean[k] + std[k] * new Vector2(nx[k], nz[k]), -Vector2.One, Vector2.One), 1);
                pool.Add(Evaluate(u));
            }
            if (iteration == 0)
                foreach (var old in _elites.Take(reused))
                {
                    var u = ControlMath.Shift(old, q.Knots, shift);
                    // Algorithm-1 elite shifting uses a random appended tail.
                    for (var k = 0; k < u.Length; ++k)
                        if (k + shift >= old.Length - 1)
                            u[k] = ControlMath.Project(new Vector2((float)(_rng.NextDouble() * 2 - 1), (float)(_rng.NextDouble() * 2 - 1)), 1);
                    pool.Add(Evaluate(u));
                }
            else pool.AddRange(elites.Take(reused));
            if (iteration == Math.Max(1, iterations) - 1) pool.Add(Evaluate((Vector2[])mean.Clone()));
            pool.Sort((a, b) => a.Score.CompareTo(b.Score));
            elites = pool.Take(kElite).ToList();
            for (var k = 0; k < q.Knots; ++k)
            {
                var m = Vector2.Zero;
                foreach (var elite in elites) m += elite.U[k];
                m /= elites.Count;
                var variance = Vector2.Zero;
                foreach (var elite in elites) { var d = elite.U[k] - m; variance += d * d; }
                variance /= elites.Count;
                mean[k] = .1f * mean[k] + .9f * m;
                std[k] = .1f * std[k] + .9f * new Vector2(MathF.Sqrt(variance.X), MathF.Sqrt(variance.Y));
            }
        }
        _mean = mean; _elites = elites.Select(e => e.U).ToArray(); _at = q.StartTime;
        LastCost = elites[0].Score.Display;
        return new(elites[0].U.Select(v => v * q.Speed).ToArray(), q.Ends);
    }

    void EnsureBasis(int h)
    {
        if (_basisSize == h) return;
        _basisSize = h; var fCount = h / 2 + 1;
        _cos = new double[h, fCount]; _sin = new double[h, fCount]; double variance = 1;
        // beta=2: power spectral density proportional to 1/f^2. The DC
        // coefficient remains random, allowing a coherent nonzero direction.
        for (var f = 1; f < fCount; ++f) variance += 1.0 / (f * f);
        _normalization = Math.Sqrt(variance);
        for (var k = 0; k < h; ++k)
            for (var f = 1; f < fCount; ++f)
            { _cos[k, f] = Math.Cos(2 * Math.PI * k * f / h) / f; _sin[k, f] = Math.Sin(2 * Math.PI * k * f / h) / f; }
    }
    float[] Colored(int h)
    {
        var a = new double[h / 2 + 1]; var b = new double[a.Length]; var dc = Normal();
        for (var f = 1; f < a.Length; ++f) { a[f] = Normal(); b[f] = Normal(); }
        var noise = new float[h];
        for (var k = 0; k < h; ++k)
        {
            var value = dc;
            for (var f = 1; f < a.Length; ++f) value += a[f] * _cos[k, f] + b[f] * _sin[k, f];
            noise[k] = (float)(value / _normalization);
        }
        return noise;
    }
    double Normal() => Math.Sqrt(-2 * Math.Log(Math.Max(1e-12, _rng.NextDouble()))) * Math.Cos(2 * Math.PI * _rng.NextDouble());
}
