namespace MasterPlayer.MechanicRuntime;

internal static class EncounterPlanningHorizon
{
    public static double Calculate(in EncounterRouteRequest request)
    {
        var q = request;
        var distance = Vector2.Distance(q.Position, q.Goal);
        // A distant duty objective remains the goal preference. Only its local
        // approach matters here; all known activation times still extend the horizon.
        if (q.Snapshot.UsesDutyTerrain)
            distance = Math.Min(distance, q.Snapshot.Arena.Radius);
        var end = Math.Max(.4, distance / q.Speed);
        void Include(DateTime at)
        {
            if (at != default && at != DateTime.MaxValue)
                end = Math.Max(end, (at - q.Now).TotalSeconds);
        }
        foreach (var hazard in q.Snapshot.Hazards)
        {
            Include(hazard.Activation);
            if (hazard.ExpiresAt is { } expiry) Include(expiry);
        }
        foreach (var knockback in q.Snapshot.Knockbacks)
        {
            Include(knockback.Source.Activation);
            if (knockback.Timing.Available && knockback.Source.Activation != default && knockback.Source.Activation != DateTime.MaxValue)
                end = Math.Max(end, (knockback.Source.Activation - q.Now).TotalSeconds
                    + knockback.Timing.ApplicationDelay + knockback.Timing.Duration);
        }
        if (q.Displacement is { } observed) Include(observed.End);
        foreach (var chase in q.Snapshot.ChasingHazards)
            if (chase.Remaining > 0 && chase.NextActivation != DateTime.MaxValue)
                Include(chase.NextActivation.AddSeconds((chase.Remaining - 1) * chase.Interval
                    + (q.Snapshot.Residence?.ReentrySeconds ?? 0)));
        if (q.Snapshot.PathMechanic is { } mechanic)
            end = Math.Max(end, mechanic.RequiredHorizon(q.Now));
        if (q.Snapshot.Hints is { } hints)
        { Include(hints.ModeActivation); Include(hints.ModeFinish); }
        // Include the last event itself and a terminal hold after that event.
        return end + DeepDungeon.MechanicPlanning.ControlMath.Knot;
    }
}
