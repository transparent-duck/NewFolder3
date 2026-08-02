using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace NewFolder3;

/// <summary>
/// Public access decision used by UI page gating and FSD start authorization.
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
    /// Instruction shown in the 說明 tab while access is denied. Empty when unused.
    /// </summary>
    string DenialInstruction { get; }

    bool TryAuthorizeFsdStart(out string error);
}

internal static class NewFolder3FsdPageAccess
{
    internal static bool CanShowFsdPage(NewFolder3AccessDecision decision)
    {
        ArgumentNullException.ThrowIfNull(decision);
        return decision.IsAllowed;
    }
}

internal sealed class NewFolder3AllowAllAccessGate : INewFolder3AccessGate
{
    public NewFolder3AccessDecision Current => NewFolder3AccessDecision.Allowed();

    public string DenialInstruction => string.Empty;

    public bool TryAuthorizeFsdStart(out string error)
    {
        error = string.Empty;
        return true;
    }
}

internal delegate INewFolder3AccessGate NewFolder3AccessGateFactory(
    IDalamudPluginInterface pluginInterface,
    IPluginLog pluginLog);
