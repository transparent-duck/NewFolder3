namespace DeepDungeon.Fsd.Runtime;

// Keep existing manual selections stable; legacy value 0 migrates to empty custom configuration.
public enum FsdRotationProvider { RotationSolverReborn = 1, WrathCombo = 2, PromeRotation = 3, Custom = 4 }
public enum FsdRotationState { Unavailable, Unknown, Off, On }

public sealed class FsdRotationSettings
{
    public FsdRotationProvider Provider { get; set; } = FsdRotationProvider.Custom;
    public string CustomEnableCommand { get; set; } = string.Empty;
    public string CustomDisableCommand { get; set; } = string.Empty;

    public void NormalizeProvider()
    {
        if (!Enum.IsDefined(Provider)) Provider = FsdRotationProvider.Custom;
    }

    public bool TryValidate(out string error)
    {
        if (!Enum.IsDefined(Provider))
            error = "請選擇自動輸出插件。";
        else if (Provider == FsdRotationProvider.Custom &&
                 (string.IsNullOrWhiteSpace(CustomEnableCommand) || string.IsNullOrWhiteSpace(CustomDisableCommand)))
            error = "請選擇自動輸出插件。";
        else if (Provider == FsdRotationProvider.Custom &&
                 (!IsSingleSlashCommand(CustomEnableCommand) || !IsSingleSlashCommand(CustomDisableCommand)))
            error = "自訂輸出需要有效的啟用及關閉指令。";
        else { error = string.Empty; return true; }
        return false;
    }

    public static bool IsSingleSlashCommand(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return false;
        ReadOnlySpan<char> value = command.AsSpan().Trim();
        return value.Length > 1 && value[0] == '/' && !char.IsWhiteSpace(value[1]) &&
            value.IndexOfAny('\r', '\n', '\0') < 0;
    }
}
