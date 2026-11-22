using FFXIVClientStructs.FFXIV.Client.Game.InstanceContent;

namespace DeepDungeon.Fsd.Dalamud;

/// <summary>One boss floor selects a strategy, calls Start before Update, and
/// calls Stop on suspension or retirement. Update receives only the current
/// frame's native director; implementations must not retain its pointer.</summary>
public unsafe interface IBossMovementStrategy
{
    string Name { get; }
    bool SuppressCombatItems { get; }
    bool RequestsNorthCamera { get; }
    void Start();
    string? Update(InstanceContentDeepDungeon* dungeon);
    void Stop();
}
