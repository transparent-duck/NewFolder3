using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace NewFolder3;

/// <summary>
/// Public access decision used by FSD start authorization.
/// Official builds may supply a stricter gate via the build-profile overlay; the
/// public default is explicit allow-all.
/// </summary>
internal sealed record NewFolder3AccessDecision(bool IsAllowed, string Reason)
{
    internal static NewFolder3AccessDecision Allowed() =>
        new(true, "Public build profile allows all access.");

    internal static NewFolder3AccessDecision Denied(string reason) =>
        new(false, reason);
}

/// <summary>
/// Host access gate abstraction. Public builds use <see cref="NewFolder3AllowAllAccessGate"/>.
/// Official overlays may inject a private implementation without exposing it publicly.
/// </summary>
internal interface INewFolder3AccessGate
{
    NewFolder3AccessDecision Current { get; }

    /// <summary>
    /// Read-only notice for the shared FSD Start/Stop UI when start is denied. Empty when unused.
    /// </summary>
    string FsdStartDenialNotice { get; }

    bool TryAuthorizeFsdStart(out string error);
}

internal sealed class NewFolder3AllowAllAccessGate : INewFolder3AccessGate
{
    public NewFolder3AccessDecision Current => NewFolder3AccessDecision.Allowed();

    public string FsdStartDenialNotice => string.Empty;

    public bool TryAuthorizeFsdStart(out string error)
    {
        error = string.Empty;
        return true;
    }
}

internal delegate INewFolder3AccessGate NewFolder3AccessGateFactory(
    IDalamudPluginInterface pluginInterface,
    IPluginLog pluginLog);
