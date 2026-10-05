using DeepDungeon.Fsd.Core;

namespace DeepDungeon.Fsd.Runtime;

public readonly record struct FsdRotationControlEvent(FsdRotationProvider Provider, bool Enabled,
    FsdRotationState Before, FsdRotationState After, bool Accepted, string Reason, uint DungeonId, byte Floor);

/// <summary>Dispatches output commands at gameplay boundaries, without enforcing a persistent switch state.</summary>
public sealed class FsdRotationController(Func<FsdRotationProvider, FsdRotationState> readState,
    Func<FsdRotationProvider, bool, string, bool> setEnabled,
    Action<FsdRotationControlEvent>? record = null)
{
    private bool _inDuty, _inCombat;
    private FsdRotationProvider _provider;
    private string _customCommand = string.Empty;
    private long _preparationSequence;
    public FsdRotationProvider? FailedProvider { get; private set; }
    public bool DesiredEnabled { get; private set; }

    public void Update(bool runActive, FarmingMode? mode, in DeepDungeonStateSnapshot state, bool inCombat,
        FsdRotationSettings settings, bool suppressOutput = false, long preparationSequence = 0,
        bool playerAvailable = true)
    {
        if (!runActive || !settings.TryValidate(out _))
        {
            Reset(); return;
        }
        // Exit resets the entry boundary, but never sends an output command.
        if (!state.IsInDeepDungeonTerritory || !state.IsInDuty)
        {
            _inDuty = _inCombat = false;
            _preparationSequence = 0;
            return;
        }
        if (!state.IsValid || state.IsTransitioning || !playerAvailable) return;
        bool enabled = !suppressOutput && state.FloorKind != DeepDungeonFloorKind.Result &&
            mode is not (FarmingMode.Aetherpool or FarmingMode.HoardDiscovery);
        string command = (enabled ? settings.CustomEnableCommand : settings.CustomDisableCommand) ?? string.Empty;
        string? reason = !_inDuty ? "duty-entry" :
            DesiredEnabled != enabled ? "output-policy-changed" :
            _provider != settings.Provider || _customCommand != command ? "configuration-changed" :
            enabled && preparationSequence > 0 && preparationSequence != _preparationSequence ? "combat-preparation" :
            enabled && inCombat && !_inCombat ? "combat-started" : null;
        _inDuty = true;
        DesiredEnabled = enabled;
        _provider = settings.Provider;
        _customCommand = command;
        _inCombat = inCombat;
        _preparationSequence = preparationSequence;
        if (reason == null) return;

        // Readback is diagnostic only: RSR includes transient native-player availability.
        var before = readState(_provider);
        bool accepted = setEnabled(_provider, enabled, command);
        var after = readState(_provider);
        FailedProvider = accepted ? null : _provider;
        record?.Invoke(new(_provider, enabled, before, after, accepted, reason, state.DungeonId, state.Floor));
    }

    public void Reset()
    {
        _inDuty = _inCombat = DesiredEnabled = false;
        _preparationSequence = 0;
        FailedProvider = null;
    }
}
