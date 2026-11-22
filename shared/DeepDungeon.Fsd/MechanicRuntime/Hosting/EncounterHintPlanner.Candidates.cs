using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Threading;
using BossMod;

namespace MasterPlayer.MechanicRuntime;

public sealed partial class EncounterHintPlanner
{
    private const int PlanExpansionBudget = 9000;
    private int _remainingExpansions;
    private int _totalExpanded;
    private long _planStarted;
    private bool _planBudgetReached;
    public int CandidateCount { get; private set; }

    private void BeginCandidateBudget()
    {
        _remainingExpansions = PlanExpansionBudget;
        _planStarted = Stopwatch.GetTimestamp(); _planBudgetReached = false;
        _totalExpanded = 0; CandidateCount = 0;
        // Hints may contain frame-scoped delegates. Reuse their sampled fields
        // within this plan only; never cache world-dependent geometry across frames.
        _shapeDistances.Clear();
        _usedFields = 0;
        _walkPrepared = false;
    }

    private bool ConsumeExpansion()
    {
        ++_totalExpanded;
        --_remainingExpansions;
        return !CandidateBudgetExpired();
    }

    private bool CandidateBudgetExpired()
    {
        _planBudgetReached |= _remainingExpansions <= 0
            || Stopwatch.GetElapsedTime(_planStarted).TotalMilliseconds >= 15;
        return _planBudgetReached;
    }

}
