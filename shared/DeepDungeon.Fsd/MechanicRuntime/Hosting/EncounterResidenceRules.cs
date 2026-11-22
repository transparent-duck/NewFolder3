namespace MasterPlayer.MechanicRuntime;

internal interface IResidenceBoundary
{
    float DistanceLowerBound(Vector2 position);
}

internal struct EncounterResidenceState
{
    public bool Active, GroundUnsafe, Reported, Immune;
    public double Deadline, SafeContact, ImmunityContact, ImmunityExit;
    public double MinimumResetSlack, LastResetAt;
}

internal readonly record struct ResidenceTransition(int Hits, double Exposure, double FirstViolation);

internal static class EncounterResidenceRules
{
    public static EncounterResidenceState Create(EncounterResidenceHazard rule, Vector2 position, DateTime now, double start)
    {
        var groundUnsafe = Distance(rule, position) < EncounterHintPlanner.HintMargin;
        var active = rule.InitialActive ?? rule.StatusPresent ?? groundUnsafe;
        var deadline = rule.InitialDeadline == DateTime.MaxValue ? double.PositiveInfinity
            : start + (rule.InitialDeadline - now).TotalSeconds;
        if (active && !double.IsFinite(deadline)) deadline = start + rule.ReentrySeconds;
        return new() { MinimumResetSlack = double.PositiveInfinity, LastResetAt = double.NegativeInfinity, Active = active, GroundUnsafe = groundUnsafe, Deadline = active ? deadline : double.PositiveInfinity,
            SafeContact = rule.InitialSafeContact, ImmunityContact = rule.InitialImmunityContact, Immune = rule.WindImmune };
    }

    public static ResidenceTransition Advance(ref EncounterResidenceState state, EncounterResidenceHazard rule,
        Vector2 from, Vector2 to, double start, double end)
    {
        var hits = 0; var exposure = 0d; var first = double.PositiveInfinity;
        var count = Math.Max(1, (int)Math.Ceiling(Vector2.Distance(from, to) / .0625f));
        Span<double> cuts = stackalloc double[4];
        for (var k = 0; k < count; ++k)
        {
            var lo = (double)k / count; var hi = (double)(k + 1) / count;
            var used = 2; cuts[0] = lo; cuts[1] = hi;
            // Split both rock-contact and certain-sand boundaries. Time spent
            // outside either region cannot count toward its registration timer.
            for (var boundary = 0; boundary < 2; ++boundary)
            {
                var margin = boundary == 0 ? EncounterHintPlanner.HintMargin : -EncounterHintPlanner.HintMargin;
                var a = lo; var b = hi; var side = Distance(rule, Vector2.Lerp(from, to, (float)a)) < margin;
                if (side == (Distance(rule, Vector2.Lerp(from, to, (float)b)) < margin)) continue;
                for (var j = 0; j < 12; ++j)
                {
                    var mid = (a + b) * .5;
                    if ((Distance(rule, Vector2.Lerp(from, to, (float)mid)) < margin) == side) a = mid; else b = mid;
                }
                cuts[used++] = (a + b) * .5;
            }
            cuts[..used].Sort();
            for (var c = 1; c < used; ++c)
            {
                var at = start + (end - start) * cuts[c - 1];
                var until = start + (end - start) * cuts[c]; var dt = until - at;
                var distance = Distance(rule, Vector2.Lerp(from, to, (float)((cuts[c - 1] + cuts[c]) * .5)));
                var safe = distance >= EncounterHintPlanner.HintMargin;
                var certainSand = distance < -EncounterHintPlanner.HintMargin;
                if (!safe)
                {
                    state.SafeContact = 0;
                    if (!state.Active) { state.Active = true; state.Deadline = at + rule.ReentrySeconds; state.Reported = false; }
                }
                var resetAfter = safe ? Math.Max(0, rule.ResetDwellSeconds - state.SafeContact) : double.PositiveInfinity;
                var activeUntil = Math.Min(until, at + resetAfter);
                if (state.Active)
                {
                    if (!state.Reported && activeUntil >= state.Deadline - 1e-6)
                    { ++hits; first = Math.Min(first, Math.Max(at, state.Deadline)); state.Reported = true; }
                    exposure += Math.Max(0, activeUntil - Math.Max(at, state.Deadline));
                }
                if (safe)
                {
                    state.SafeContact += dt;
                    if (state.SafeContact + 1e-9 >= rule.ResetDwellSeconds)
                    {
                        if (state.Active)
                        {
                            state.LastResetAt = at + resetAfter;
                            state.MinimumResetSlack = Math.Min(state.MinimumResetSlack, state.Deadline - state.LastResetAt);
                        }
                        state.Active = false; state.Deadline = double.PositiveInfinity; state.Reported = false;
                    }
                }
                state.GroundUnsafe = !safe;
                state.ImmunityContact = certainSand ? state.ImmunityContact + dt : 0;
                state.ImmunityExit = certainSand ? 0 : state.ImmunityExit + dt;
                if (certainSand && state.ImmunityContact + 1e-9 >= rule.ImmunityGrantSeconds) state.Immune = true;
                else if (!certainSand && state.ImmunityExit + 1e-9 >= rule.ImmunityLossSeconds) state.Immune = false;
            }
        }
        return new(hits, exposure, first);
    }
    private static float Distance(EncounterResidenceHazard rule, Vector2 p) => rule.UnsafeRegion.Distance(new(p.X, p.Y));
}
