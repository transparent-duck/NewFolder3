using DeepDungeon.Fsd.Core;

namespace DeepDungeon.Fsd.Runtime;

/// <summary>Dispatches only on policy changes; never sends commands on every frame.</summary>
public sealed class FsdBossMechanicsController
{
    private readonly Func<string, bool> _dispatch;
    private bool _hasPolicy;
    private bool _runWasActive;
    private bool _desiredEnabled;
    private string _enableCommand = string.Empty;
    private string _disableCommand = string.Empty;

    public FsdBossMechanicsController(Func<string, bool> dispatch) => _dispatch = dispatch;
    public bool IsEnabled { get; private set; }

    public void Update(bool runActive, in DeepDungeonStateSnapshot state, FsdBossMechanicsSettings settings, FarmingMode? mode = null)
    {
        if (!runActive)
        {
            Stop();
            return;
        }
        _runWasActive = true;
        if (!_hasPolicy)
            _disableCommand = settings.GetDisableCommand() ?? string.Empty;
        if (!state.IsValid || state.IsTransitioning)
            return;
        if (!state.IsInDeepDungeonTerritory || !state.IsInDuty)
        {
            if (_hasPolicy && _desiredEnabled)
            {
                Send(_disableCommand);
                _desiredEnabled = IsEnabled = false;
            }
            return;
        }
        if (state.FloorKind == DeepDungeonFloorKind.Unknown)
            return;

        string on = settings.GetEnableCommand() ?? string.Empty;
        string off = settings.GetDisableCommand() ?? string.Empty;
        bool enabled = mode == FarmingMode.DeepProgression || state.FloorKind == DeepDungeonFloorKind.Boss;
        bool changed = on != _enableCommand || off != _disableCommand;
        if (_hasPolicy && !changed && enabled == _desiredEnabled)
            return;
        if (_hasPolicy && changed && _desiredEnabled)
            Send(_disableCommand);
        _enableCommand = on;
        _disableCommand = off;
        _desiredEnabled = enabled;
        _hasPolicy = true;
        bool dispatched = Send(enabled ? on : off);
        IsEnabled = enabled && dispatched;
    }

    public void Stop()
    {
        if (!_runWasActive)
            return;
        Send(_disableCommand);
        _hasPolicy = _desiredEnabled = IsEnabled = false;
        _runWasActive = false;
    }

    private bool Send(string command) =>
        !string.IsNullOrWhiteSpace(command) && _dispatch(command);
}
