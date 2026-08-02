using Dalamud.Plugin.Services;
using DeepDungeon.Fsd.Core;
using DeepDungeon.Fsd.Dalamud.GameState;
using DeepDungeon.Fsd.Dalamud.Runtime.Entry;

namespace DeepDungeon.Fsd.Dalamud.Runtime.Scenarios;

internal sealed class ControlledPt21To30Scenario : IScenario
{
    private readonly ControlledPtSurveySession _session;
    private RunContext? _context;
    private ControlledPtOccupiedEntryFlow? _entry;
    private PilgrimsTraverseRestExitFlow? _restExit;
    private ControlledPtSaveVerificationFlow? _verification;
    private bool _wasInDuty;
    private bool _entryValidated;
    private bool _complete;
    private bool _verificationPrepared;

    public ControlledPt21To30Scenario(ControlledPtSurveySession session)
    {
        _session = session;
    }

    public string Name => "PT 21-30 controlled capture";
    public bool IsComplete => _complete;
    public bool ShouldLoop => true;
    public bool RequiresDutyCompletionEvent => false;

    public void Initialize(RunContext context)
    {
        _context = context;
        context.ResetAttemptState();
        _session.BeginAttempt();
        context.ControlledPtSurvey = _session;
        context.RunOptions.Set(new RunOptions
        {
            OpenGold = false,
            OpenSilver = false,
            OpenBronze = false,
            BandedEnabled = false,
            LeaveMode = LeaveMode.AfterFinishDungeon,
            LeaveAfterMinutes = 0,
            RequireValidatedAbandonPrompt = true
        });

        _entry = new ControlledPtOccupiedEntryFlow(_session);
        _entry.Prepare(context);
        _restExit = null;
        _verification = null;
        _wasInDuty = false;
        _entryValidated = false;
        _complete = false;
        _verificationPrepared = false;
    }

    public void Update(IFramework framework)
    {
        var context = _context;
        if (context == null)
            return;
        if (context.StatusIsError)
        {
            _complete = true;
            return;
        }

        if (!_wasInDuty)
        {
            if (!context.Duty.IsInDuty)
            {
                _entry?.Update(framework);
                if (context.StatusIsError)
                    _complete = true;
                return;
            }

            _wasInDuty = true;
        }

        if (context.Duty.IsInDuty)
        {
            if (context.Duty.IsTransitioning || context.Duty.Floor == 0)
                return;

            // Cleanup is independent of floor automation: it runs on the same stable
            // update and does not gate item use, but never dispatches addon callbacks
            // while the game is zoning.
            DeepDungeonUi.TryCloseAddon("DeepDungeonSaveData");
            DeepDungeonUi.TryCloseAddon("DeepDungeonMenu");

            if (!_entryValidated)
            {
                _entryValidated = true;
                if (context.Duty.DungeonId != DungeonCatalog.PilgrimsTraverse.DungeonId ||
                    context.Duty.Floor != ControlledPtSurveyPolicy.FirstFloor)
                {
                    RequestFatalLeave(
                        $"Controlled PT capture entered dungeon={context.Duty.DungeonId}, floor={context.Duty.Floor}; expected Pilgrim's Traverse floor 21.");
                    return;
                }
            }

            if (context.Duty.Floor >= 30)
            {
                RequestFatalLeave("Controlled PT capture reached protected floor 30; abandoning immediately.");
                return;
            }

            if (_session.Fatal)
            {
                RequestLeave();
                return;
            }

            if (_session.LeaveRequested)
            {
                RequestLeave();
                return;
            }

            return;
        }

        _restExit ??= new PilgrimsTraverseRestExitFlow(requireValidatedConfirmation: true);
        if (!_restExit.IsPrepared)
            _restExit.Prepare(context);
        if (!_restExit.Update(framework))
            return;

        _verification ??= new ControlledPtSaveVerificationFlow(_session);
        if (!_verificationPrepared)
        {
            _verification.Prepare(context);
            _verificationPrepared = true;
        }
        if (!_verification.Update(framework))
            return;

        if (_session.Fatal)
        {
            context.StatusLine = _session.FatalReason;
            context.StatusIsError = true;
        }
        else
        {
            _session.MarkAttemptSucceeded();
            context.StatusLine = "Controlled PT capture attempt persisted; reusable save verified.";
        }
        _complete = true;
    }

    public void Dispose()
    {
        try { _entry?.Reset(); } catch { }
        try { _restExit?.Dispose(); } catch { }
        try { _verification?.Reset(); } catch { }
        if (_context != null)
            _context.ControlledPtSurvey = null;
        _context = null;
    }

    private void RequestFatalLeave(string reason)
    {
        _session.Fail(reason);
        if (_context != null)
            _context.StatusLine = reason;
        RequestLeave();
    }

    private void RequestLeave()
    {
        _context?.RunOptions.Update(options =>
        {
            options.LeaveMode = LeaveMode.Immediate;
            options.RequireValidatedAbandonPrompt = true;
        });
    }
}
