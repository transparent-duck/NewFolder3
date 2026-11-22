using System;
using System.Numerics;
using DeepDungeon.Fsd.Core;
using DeepDungeon.Fsd.Dalamud.Runtime;

namespace DeepDungeon.Fsd.Dalamud.Coord;

/// <summary>
/// One committed piece of site work — a hoard candidate probe, an exact
/// hoard/indicator reveal point, or a room intel visit. Travel uses the
/// shared NavigateOp protocol; completion is the real postcondition of the
/// work — candidate probes settle at 1.4m/3s + a newer object refresh,
/// intel at 2.5m/1s, while exact reveal work completes the moment the
/// coffer is actually observed or the hoard is natively acquired,
/// wherever the player stands. The captured token carries the outcome
/// back to the work that commissioned it.
/// </summary>
internal sealed class SiteWorkOp : FloorOperation
{
    private const int OpPriority = 10;
    private const int MaxNavAttempts = 6;
    private static readonly NavigationRange CandidateRange = new(1.4f);
    private const float CandidateDwellSeconds = 3f;
    private static readonly NavigationRange RoomProbeRange = new(2.5f);
    private const float IntelSettleSeconds = 1f;
    private const float RevealMatchRadius = 4f;

    private readonly ObjectiveSite _site;
    private readonly SiteWorkToken _token;
    private readonly Func<bool> _hoardAcquired;
    private NavigateOp _nav;
    private float _dwellSec;
    private bool _dwelled;
    private long _evidenceAtDwell = -1;
    private DateTime _lastTickUtc = DateTime.MinValue;
    // Last evidence sequence the exact-reveal check was evaluated against —
    // recomputing the locator scan every frame while traveling is wasted
    // CPU; only a NEW observed snapshot can change the answer.
    private long _lastRevealEvidenceSequence = long.MinValue;

    internal SiteWorkOp(in ObjectiveSite site, SiteWorkToken token, Func<bool> hoardAcquired)
        : base(ObjectiveFor(site.Kind), OpPriority, MaxNavAttempts)
    {
        _site = site;
        _token = token;
        _hoardAcquired = hoardAcquired;
        var roomProbe = site.Kind is ObjectiveSiteKind.RoomProbe or ObjectiveSiteKind.PassageSearch;
        _nav = new NavigateOp(site.Position, OpPriority, MaxNavAttempts,
            roomProbe ? RoomProbeRange.Radius : CandidateRange.Radius);
    }

    internal SiteWorkToken Token => _token;
    internal string WorkKey => _token.Key;
    internal ObjectiveSiteKind SiteKind => _site.Kind;

    /// <summary>Partial dwell or a dwell waiting on its fresh-evidence
    /// postcondition is real work — the head stays committed through it.</summary>
    internal override bool ProtectsCommit => _dwellSec > 0f;

    private static FloorObjectiveKind ObjectiveFor(ObjectiveSiteKind kind) =>
        kind switch
        {
            ObjectiveSiteKind.Hoard or ObjectiveSiteKind.HoardIndicator
                => FloorObjectiveKind.CompleteKnownHoard,
            ObjectiveSiteKind.PassageSearch => FloorObjectiveKind.FindPassage,
            _ => FloorObjectiveKind.DiscoverHoard,
        };

    internal override string Describe() =>
        $"{_site.Kind} {_token.Key} dwell={_dwellSec:0.0} nav={_nav.Attempts}/{_nav.MaxAttempts}";

    internal override OpStatus Tick(in FloorFacts facts, INativeBoundary native, DateTime now,
        in OpAuthority authority)
    {
        var dt = _lastTickUtc == DateTime.MinValue ? 0f
            : Math.Clamp((float)(now - _lastTickUtc).TotalSeconds, 0f, 0.5f);
        _lastTickUtc = now;

        var isRoomProbe = _site.Kind is ObjectiveSiteKind.RoomProbe or ObjectiveSiteKind.PassageSearch;
        var range = isRoomProbe ? RoomProbeRange : CandidateRange;
        var needed = isRoomProbe ? IntelSettleSeconds : CandidateDwellSeconds;
        var inside = range.HasArrived(facts.PlayerPosition, _site.Position);
        var exactReveal =
            _site.Kind is ObjectiveSiteKind.Hoard or ObjectiveSiteKind.HoardIndicator;

        // Real postcondition BEFORE the dwell machine: exact reveal work is
        // done the moment the coffer is observed or the hoard is natively
        // acquired — a completed dwell is not a precondition.
        if (exactReveal)
        {
            if (_hoardAcquired())
                return OpStatus.Succeeded;
            if (facts.Evidence is { Available: true } revealEv &&
                revealEv.RefreshSequence != _lastRevealEvidenceSequence)
            {
                _lastRevealEvidenceSequence = revealEv.RefreshSequence;
                var foundBanded = Runtime.Search.BandedChestLocator.TryFindNearestAround(
                        revealEv, _site.Position, RevealMatchRadius,
                        out var bandedPosition) && bandedPosition.HasValue;
                if (foundBanded)
                    return OpStatus.Succeeded;
            }
        }

        if (!_dwelled)
        {
            // Valid dwell counts only physical presence; leaving the radius
            // resets it. Being inside while a higher-priority claim masks
            // movement still counts — the standing is real.
            if (inside)
            {
                _dwellSec += dt;
                if (_dwellSec >= needed)
                {
                    _dwelled = true;
                    _evidenceAtDwell = facts.Evidence?.RefreshSequence ?? -1;
                }
            }
            else
            {
                _dwellSec = 0f;
            }
        }

        if (_dwelled)
        {
            if (exactReveal)
            {
                // Its postcondition was already evaluated above — an
                // unfinished reveal simply waits where it is, or resumes
                // when the player walked away mid-dwell. There is no
                // timeout success and no abandonment.
                if (!inside)
                {
                    _dwelled = false;
                    _dwellSec = 0f;
                    _evidenceAtDwell = -1;
                }
                else
                {
                    Claim = MoveClaim.Hold(Priority);
                    return OpStatus.Running;
                }
            }
            else
            {
                // Postcondition evaluation needs a NEWER object refresh
                // than the dwell finish — stale evidence never completes
                // work, and there is no timeout success.
                var fresh = facts.Evidence is { Available: true, PlayerPosition: { } observedPosition } ev &&
                    ev.RefreshSequence > _evidenceAtDwell &&
                    range.HasArrived(observedPosition, _site.Position);
                if (!inside)
                {
                    _dwelled = false;
                    _dwellSec = 0f;
                    _evidenceAtDwell = -1;
                }
                else if (!fresh)
                {
                    Claim = MoveClaim.Hold(Priority);
                    return OpStatus.Running;
                }
                // Candidate probe (3s dwell) / room intel (1s settle):
                // fresh result observed → done. "Checked" is the
                // postcondition, not "hoard found".
                else return OpStatus.Succeeded;
            }
        }

        var navStatus = _nav.Tick(facts, native, now, authority);
        if (navStatus == OpStatus.Failed)
        {
            Failure = _nav.Failure;
            return OpStatus.Failed;
        }
        Claim = inside ? MoveClaim.Hold(Priority) : _nav.Claim;
        return OpStatus.Running;
    }

    internal override void OnSuspended()
    {
        base.OnSuspended();
        // A partial dwell must not accumulate across the suspension gap;
        // completed dwell and facts already live in FloorKnowledge.
        if (!_dwelled)
            _dwellSec = 0f;
        _lastTickUtc = DateTime.MinValue;
    }
    internal override void OnResumed(DateTime now) => _lastTickUtc = now;
    internal override void OnNavFailed(DateTime now) => _nav.OnNavFailed(now);
}
