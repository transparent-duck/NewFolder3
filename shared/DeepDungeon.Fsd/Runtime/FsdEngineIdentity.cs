using System.Reflection;

namespace DeepDungeon.Fsd.Runtime;

public static class FsdEngineIdentity
{
    private static readonly Assembly Assembly = typeof(FsdEngineIdentity).Assembly;

    public static string InformationalVersion { get; } =
        Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? Assembly.GetName().Version?.ToString()
        ?? throw new InvalidOperationException("FSD runtime assembly has no version identity.");
}
