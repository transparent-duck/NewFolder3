using BossMod;

namespace MasterPlayer.MechanicRuntime;

/// <summary>
/// Bridges an anticipated cast into an observed status. The short handoff limit
/// covers missing/immune hits; an observed status is held until it disappears.
/// Callers identify the action and supply its target radius, not floor numbers.
/// </summary>
public sealed class CastStatusRestriction(WorldState world, uint statusId,
    ExecutionRestrictions restrictions, float targetRadius = 0)
{
    private sealed class Pending(Actor caster, ActorCastInfo cast, DateTime finish)
    {
        public readonly Actor Caster = caster;
        public readonly ActorCastInfo Cast = cast;
        public readonly DateTime ExpectedFinish = finish;
        public DateTime HandoffUntil = finish.AddSeconds(2);
        public bool Finished;
        public HashSet<ulong>? Affected;
    }

    private readonly List<Pending> _pending = [];

    public void CastStarted(Actor caster)
    {
        if (caster.CastInfo is not { } cast) return;
        _pending.RemoveAll(p => p.Caster == caster);
        _pending.Add(new(caster, cast, world.FutureTime(Math.Max(0, cast.NPCRemainingTime))));
    }

    public void CastFinished(Actor caster)
    {
        foreach (var pending in _pending)
        {
            if (pending.Caster != caster) continue;
            pending.Finished = true;
            // A genuinely early interruption has no pending hit to wait for.
            pending.HandoffUntil = !pending.Cast.EventHappened
                && world.CurrentTime.AddSeconds(.3) < pending.ExpectedFinish
                ? world.CurrentTime : world.FutureTime(2);
        }
    }

    public void CastResolved(Actor caster, ActorCastEvent ev)
    {
        var pending = _pending.Find(p => p.Caster == caster);
        if (pending == null)
        {
            // Also handles joining/reloading after the cast began.
            pending = new(caster, new() { TargetID = ev.MainTargetID, Location = ev.TargetPos }, world.CurrentTime);
            _pending.Add(pending);
        }
        pending.Finished = true;
        pending.HandoffUntil = world.FutureTime(2);
        pending.Affected = [];
        foreach (var target in ev.Targets)
            pending.Affected.Add(target.ID);
    }

    public ExecutionRestrictions Get(Actor player)
    {
        if (player.IsDead) { _pending.Clear(); return ExecutionRestrictions.None; }
        var status = player.FindStatus(statusId);
        var result = status != null ? restrictions : ExecutionRestrictions.None;
        for (var i = _pending.Count - 1; i >= 0; --i)
        {
            var pending = _pending[i];
            var resolved = pending.Affected != null;
            var hasStatus = status is { } active && active.SourceID == pending.Caster.InstanceID;
            if (world.CurrentTime >= pending.HandoffUntil
                || !resolved && !pending.Finished && (pending.Caster.IsDead || pending.Caster.IsDestroyed)
                || pending.Finished && hasStatus)
            {
                _pending.RemoveAt(i);
                continue;
            }
            if (pending.Affected is { } affected)
            {
                if (affected.Contains(player.InstanceID)) result |= restrictions;
                continue;
            }
            var target = world.Actors.Find(pending.Cast.TargetID);
            var center = target?.Position ?? pending.Cast.LocXZ;
            if (pending.Cast.TargetID == player.InstanceID
                || targetRadius > 0 && player.Position.InCircle(center, targetRadius))
                result |= restrictions;
        }
        return result;
    }
}
