using System;
using System.Numerics;
using DeepDungeon.Fsd.Core;
using DeepDungeon.Fsd.Dalamud.Runtime.Floor;

namespace DeepDungeon.Fsd.Dalamud.Coord;

/// <summary>Chooses a combat target or searches for one while quota work is active.</summary>
internal sealed class ChaseMobsOp : FloorOperation
{
    private const int OpPriority = 10;
    private const int NavBudgetPerTarget = 4;
    private bool _navigationClaimed;
    private QuotaSearchRoom? _searchRoom;
    private NavigateOp? _searchNav;
    private DateTime _searchArrivedAt, _nextSearch;

    public ChaseMobsOp(FloorObjectiveKind objective)
        : base(objective, OpPriority, NavBudgetPerTarget) { }

    internal override OpStatus Tick(in FloorFacts facts, INativeBoundary native, DateTime now,
        in OpAuthority authority)
    {
        Claim = MoveClaim.None;
        _navigationClaimed = false;
        if (facts.PassageOpen && !facts.InCombat) return OpStatus.Succeeded;
        if (Failure.HasValue) return OpStatus.Failed;
        var target = native.ChaseTarget(facts.PlayerPosition, allowNewTargets: !facts.PassageOpen);
        if (target is not { } t)
        {
            return SearchForQuota(facts, native, now, authority);
        }
        _searchRoom = null;
        _searchNav = null;
        _searchArrivedAt = default;
        if (!facts.PlayerDead && authority.MaskedDt == 0)
            Claim = new MoveClaim(Priority, new AttackTargetVerb(t.GameObjectId, t.LivePosition));
        return OpStatus.Running;
    }

    private OpStatus SearchForQuota(in FloorFacts facts, INativeBoundary native, DateTime now,
        in OpAuthority authority)
    {
        Claim = MoveClaim.Hold(Priority);
        if (facts.PassageOpen || facts.InCombat || facts.PlayerDead || authority.MaskedDt > 0f)
        {
            _searchArrivedAt = default;
            return OpStatus.Running;
        }
        var search = native.Search;
        if (search == null) return OpStatus.Running;
        if (_searchRoom == null)
        {
            if (now < _nextSearch) return OpStatus.Running;
            _nextSearch = now.AddMilliseconds(500);
            var candidate = search.NextQuotaSearchRoom(facts.PlayerPosition);
            if (!candidate.Available) return OpStatus.Running;
            if (candidate.Room == null)
            {
                // The boundary reset this patrol. Keep quota work alive and
                // pick the first room of the next pass at the normal cadence.
                return OpStatus.Running;
            }
            _searchRoom = candidate.Room;
        }
        var room = _searchRoom.Value;
        // The room selects a search destination, not an arrival condition.
        // Grid routing already crosses open connections without room-index updates.
        _searchNav ??= new NavigateOp(room.Center, OpPriority, NavBudgetPerTarget, 2.5f);
        var status = _searchNav.Tick(facts, native, now, authority);
        if (status == OpStatus.Failed)
        {
            FinishQuotaSearch(false);
        }
        else if (status == OpStatus.Succeeded)
        {
            // Allow another object observation after spatial arrival. The
            // ordinary target query runs first every tick, regardless of room ID.
            if (_searchArrivedAt == default) _searchArrivedAt = now;
            if (now - _searchArrivedAt >= TimeSpan.FromSeconds(1)) FinishQuotaSearch(true);
        }
        else
        {
            _searchArrivedAt = default;
            _navigationClaimed = true;
            Claim = _searchNav.Claim;
        }
        return OpStatus.Running;

        void FinishQuotaSearch(bool reached)
        {
            search.CompleteQuotaSearchRoom(room.RoomIndex, reached);
            _searchRoom = null;
            _searchNav = null;
            _searchArrivedAt = default;
        }
    }

    internal override void OnPreempted() { base.OnPreempted(); _searchArrivedAt = default; }
    internal override void OnSuspended() { base.OnSuspended(); _searchArrivedAt = default; }
    internal override void OnNavFailed(DateTime now)
    {
        if (_navigationClaimed) _searchNav?.OnNavFailed(now);
        else Failure = FailureKind.Blocked;
    }
}
