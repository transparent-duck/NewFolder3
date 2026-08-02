namespace DeepDungeon.Fsd.Runtime;

/// <summary>
/// Optional host-supplied authorization checked before an FSD start begins side effects.
/// Return <c>false</c> to deny. The engine reports a generic start failure and does not
/// surface host-private denial detail through shared UI or bridge results.
/// </summary>
public delegate bool FsdStartAuthorizationCallback(out string error);
