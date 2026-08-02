namespace DeepDungeon.Fsd.Runtime;

public sealed record FsdApplicationSnapshot(
    string HostIdentity,
    string HostVersion,
    string EngineVersion,
    bool ExecutionLeaseHeld,
    object State);
