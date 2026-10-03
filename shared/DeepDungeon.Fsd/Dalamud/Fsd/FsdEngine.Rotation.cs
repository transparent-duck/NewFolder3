using global::Dalamud.Plugin.Ipc;
using global::Dalamud.Game.ClientState.Conditions;

namespace DeepDungeon.Fsd.Dalamud;

internal partial class FsdEngine
{
    private static ICallGateSubscriber<bool>? _wrathRotationState, _rsrRotationState, _promeRotationState, _promeRotationStop, _promeRotationStart;
    private static ICallGateSubscriber<string, object>? _rsrRotationMode;
    private static readonly DateTime[] RotationErrorRetryAt = new DateTime[5];
    private bool _bossRotationSuppressed;

    private bool SetBossMovementOverride(bool active)
    {
        if (active && !_configuration.BossMechanics.UsesBmrBossHandling()) return false;
        bool accepted = _bossMechanics.SetInternalMovementOverride(active);
        _configuration.BossMechanicsActive = _bossMechanics.IsEnabled;
        return accepted;
    }

    private void UpdateCompanionControls()
    {
        if (!IsRunActive || !_configuration.BossMechanics.UsesBmrBossHandling()) _bossRotationSuppressed = false;
        var mode = _ddHost?.Context?.FarmingPlan?.Mode;
        _bossMechanics.Update(IsRunActive, _currentDeepDungeonState, _configuration.BossMechanics, mode);
        if (IsRunActive && !_configuration.Rotation.TryValidate(out var error))
        {
            StopForRotationError(error);
            return;
        }
        _rotationControl.Update(IsRunActive, mode, _currentDeepDungeonState,
            Service.Condition[ConditionFlag.InCombat], _configuration.Rotation, DateTime.UtcNow, _bossRotationSuppressed);
        if (IsRunActive && _rotationControl.FailedProvider is { } failed)
            StopForRotationError($"無法{(_rotationControl.DesiredEnabled ? "啟用" : "關閉")}輸出：{RotationProviderLabels[(int)failed - 1]}。");
    }

    private void StopForRotationError(string error)
    {
        _farmingError = error;
        Service.Log.Warning($"[FSD.Rotation] Stopping FSD: {error}");
        _ddHost?.StopFsd();
    }

    private bool TryValidateRotation(out string error)
    {
        var settings = _configuration.Rotation;
        if (!settings.TryValidate(out error)) return false;
        if (settings.Provider == FsdRotationProvider.Custom)
        {
            if (HasRegisteredCommand(settings.CustomEnableCommand) && HasRegisteredCommand(settings.CustomDisableCommand)) return true;
            error = "自訂輸出指令不可用，請確認插件已載入。";
            return false;
        }
        if (ReadRotationState(settings.Provider) != FsdRotationState.Unavailable) return true;
        error = $"自動輸出插件不可用：{RotationProviderLabels[(int)settings.Provider - 1]}。";
        return false;
    }

    private static bool HasRegisteredCommand(string command)
    {
        string value = command.Trim();
        int end = 0;
        while (end < value.Length && !char.IsWhiteSpace(value[end])) end++;
        return Service.CommandManager.Commands.ContainsKey(value[..end]);
    }

    private static FsdRotationState ReadRotationState(FsdRotationProvider provider)
    {
        if (provider == FsdRotationProvider.Custom) return FsdRotationState.Unknown;
        try
        {
            ICallGateSubscriber<bool> read;
            switch (provider)
            {
                case FsdRotationProvider.RotationSolverReborn:
                    _rsrRotationMode ??= Service.PluginInterface.GetIpcSubscriber<string, object>("RotationSolverReborn.ChangeOperatingMode");
                    if (!_rsrRotationMode.HasAction) return FsdRotationState.Unavailable;
                    read = _rsrRotationState ??= Service.PluginInterface.GetIpcSubscriber<bool>("RotationSolverReborn.AutorotationActive");
                    break;
                case FsdRotationProvider.WrathCombo:
                    if (!Service.CommandManager.Commands.ContainsKey("/wrath")) return FsdRotationState.Unavailable;
                    read = _wrathRotationState ??= Service.PluginInterface.GetIpcSubscriber<bool>("WrathCombo.GetAutoRotationState");
                    break;
                case FsdRotationProvider.PromeRotation:
                    _promeRotationStart ??= Service.PluginInterface.GetIpcSubscriber<bool>("PromeRotation.IPC.Start");
                    _promeRotationStop ??= Service.PluginInterface.GetIpcSubscriber<bool>("PromeRotation.IPC.Stop");
                    if (!_promeRotationStart.HasFunction || !_promeRotationStop.HasFunction) return FsdRotationState.Unavailable;
                    read = _promeRotationState ??= Service.PluginInterface.GetIpcSubscriber<bool>("PromeRotation.IPC.IsRunning");
                    break;
                default: return FsdRotationState.Unavailable;
            }
            return !read.HasFunction ? FsdRotationState.Unknown : read.InvokeFunc() ? FsdRotationState.On : FsdRotationState.Off;
        }
        catch { return FsdRotationState.Unavailable; }
    }

    private static bool SetRotationEnabled(FsdRotationProvider provider, bool enabled, string customCommand)
    {
        try
        {
            bool succeeded;
            if (provider == FsdRotationProvider.RotationSolverReborn)
            {
                // Dalamud converts the string to the provider's enum. Direct mode setting avoids /rotation Auto's toggle policy.
                (_rsrRotationMode ??= Service.PluginInterface.GetIpcSubscriber<string, object>("RotationSolverReborn.ChangeOperatingMode"))
                    .InvokeAction(enabled ? "Auto" : "Off");
                succeeded = true;
            }
            else succeeded = provider switch
            {
                FsdRotationProvider.WrathCombo => Service.CommandManager.ProcessCommand(enabled ? "/wrath auto on" : "/wrath auto off"),
                FsdRotationProvider.PromeRotation => (enabled
                    ? _promeRotationStart ??= Service.PluginInterface.GetIpcSubscriber<bool>("PromeRotation.IPC.Start")
                    : _promeRotationStop ??= Service.PluginInterface.GetIpcSubscriber<bool>("PromeRotation.IPC.Stop")).InvokeFunc(),
                FsdRotationProvider.Custom => FsdRotationSettings.IsSingleSlashCommand(customCommand) &&
                    HasRegisteredCommand(customCommand) && Service.CommandManager.ProcessCommand(customCommand.Trim()),
                _ => false
            };
            if (succeeded) Service.Log.Info($"[FSD.Rotation] {(enabled ? "Enable" : "Disable")} request accepted for {provider}; stop will not restore output.");
            else LogRotationFailure(provider, "mode request was not accepted");
            return succeeded;
        }
        catch (Exception error)
        {
            LogRotationFailure(provider, error.Message);
            return false;
        }
    }

    private static void LogRotationFailure(FsdRotationProvider provider, string error)
    {
        var now = DateTime.UtcNow;
        if (now < RotationErrorRetryAt[(int)provider]) return;
        RotationErrorRetryAt[(int)provider] = now.AddSeconds(5);
        Service.Log.Warning($"[FSD.Rotation] Cannot set output for {provider}: {error}");
    }
}
