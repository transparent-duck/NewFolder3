using global::Dalamud.Game.ClientState.Objects;
using global::Dalamud.Game.ClientState.Objects.SubKinds;
using global::Dalamud.IoC;
using global::Dalamud.Plugin;
using global::Dalamud.Plugin.Services;

namespace DeepDungeon.Fsd.Dalamud;

internal sealed class Service
{
    internal const uint ActionStatus_Ready = 0;
    internal static readonly uint[] Available_GetActinoInRangeOrLoSStatus = [0, 565];
    internal const uint Item_OrthosPotion = 38944;
    internal const uint Item_PilgrimsPotion = 47102;
    internal const uint Item_SustainingPotion = 20309;
    internal const uint Item_EmpyreanPotion = 23163;

    [PluginService] internal static ISigScanner Scanner { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static IObjectTable GameObjects { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;
    [PluginService] internal static IChatGui Chat { get; private set; } = null!;
    [PluginService] internal static IGameGui GameGui { get; private set; } = null!;
    [PluginService] internal static IGameInteropProvider GameInteropProvider { get; private set; } = null!;
    [PluginService] internal static ICondition Condition { get; private set; } = null!;
    [PluginService] internal static IPartyList PartyList { get; private set; } = null!;
    [PluginService] internal static ITargetManager TargetManager { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] internal static ITextureProvider TextureProvider { get; private set; } = null!;
    [PluginService] internal static IDutyState DutyState { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;

    internal static IPlayerCharacter? LocalPlayer => GameObjects.LocalPlayer;
}
