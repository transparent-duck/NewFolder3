namespace DeepDungeon.Fsd.Core;

public enum SightUseState
{
    None,
    Attempted,
    Confirmed
}

public static class SightUseStateMachine
{
    public static SightUseState MarkAttempted(SightUseState state) =>
        state == SightUseState.None ? SightUseState.Attempted : state;

    public static SightUseState MarkConfirmed(SightUseState state) =>
        SightUseState.Confirmed;

    public static bool PreventsAutomaticUse(SightUseState state) =>
        state is SightUseState.Attempted or SightUseState.Confirmed;
}
