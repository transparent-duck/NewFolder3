namespace DeepDungeon.Fsd.Runtime;

public enum FsdBossMechanicsProvider
{
    Bmr,
    Custom
}

public sealed class FsdBossMechanicsSettings
{
    public FsdBossMechanicsProvider Provider { get; set; }
    public string CustomEnableCommand { get; set; } = string.Empty;
    public string CustomDisableCommand { get; set; } = string.Empty;

    public string GetEnableCommand() => Provider == FsdBossMechanicsProvider.Bmr ? "/bmrai on" : CustomEnableCommand;
    public string GetDisableCommand() => Provider == FsdBossMechanicsProvider.Bmr ? "/bmrai off" : CustomDisableCommand;
}
