using DeepDungeon.Fsd.Core;

namespace DeepDungeon.Fsd.Runtime;

/// <summary>Applies the active mode's output policy. Stopping never restores a previous state.</summary>
public sealed class FsdRotationController(Func<FsdRotationProvider, FsdRotationState> readState,
    Func<FsdRotationProvider, bool, string, bool> setEnabled)
{
    private bool _applied;
    private bool _active, _inCombat;
    private FsdRotationProvider _provider;
    private string _customCommand = string.Empty;
    private uint _dungeonId;
    private byte _floor;
    private DateTime _nextCheck;
    public FsdRotationProvider? FailedProvider { get; private set; }
    public bool DesiredEnabled { get; private set; }

    public void Update(bool runActive, FarmingMode? mode, in DeepDungeonStateSnapshot state, bool inCombat,
        FsdRotationSettings settings, DateTime nowUtc, bool suppressOutput = false)
    {
        if (!runActive || !settings.TryValidate(out _))
        {
            Reset();
            return;
        }
        if (state.IsTransitioning) return;
        bool enabled = !suppressOutput && mode is not (FarmingMode.Aetherpool or FarmingMode.HoardDiscovery);
        string command = (enabled ? settings.CustomEnableCommand : settings.CustomDisableCommand) ?? string.Empty;
        bool force = !_active || DesiredEnabled != enabled || _provider != settings.Provider || _customCommand != command ||
            state.IsValid && (_dungeonId != state.DungeonId || _floor != state.Floor) ||
            inCombat && !_inCombat;
        if (!force && nowUtc < _nextCheck) return;
        _active = true;
        DesiredEnabled = enabled;
        _provider = settings.Provider;
        _customCommand = command;
        _inCombat = inCombat;
        if (state.IsValid) { _dungeonId = state.DungeonId; _floor = state.Floor; }
        _nextCheck = nowUtc.AddSeconds(1);
        if (force) { _applied = false; FailedProvider = null; }
        if (!Enum.IsDefined(_provider)) { FailedProvider = _provider; return; }
        Apply(force);
    }

    private void Apply(bool force)
    {
        var state = readState(_provider);
        if (state == FsdRotationState.Unavailable) { FailedProvider = _provider; return; }
        var desired = DesiredEnabled ? FsdRotationState.On : FsdRotationState.Off;
        // Already-on output stays on. Off boundaries still clear providers' pending actions.
        if (state == desired && (DesiredEnabled || !force)) { FailedProvider = null; return; }
        if (state == FsdRotationState.Unknown && _applied && !force) return;
        bool succeeded = setEnabled(_provider, DesiredEnabled, _customCommand);
        if (succeeded)
        {
            var observed = readState(_provider);
            succeeded = observed != FsdRotationState.Unavailable &&
                (observed == FsdRotationState.Unknown || observed == desired);
        }
        _applied = succeeded;
        FailedProvider = succeeded ? null : _provider;
    }

    public void Reset()
    {
        if (!_active) return;
        _active = _inCombat = DesiredEnabled = false;
        _dungeonId = 0; _floor = 0;
        _nextCheck = default;
        FailedProvider = null;
        _applied = false;
    }
}
