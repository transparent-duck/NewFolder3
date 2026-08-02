using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using DeepDungeon.Fsd.Dalamud;
using OmenTools;
using OmenTools.OmenService;

namespace NewFolder3;

public sealed class Plugin : IDalamudPlugin, IDisposable
{
    private readonly IDalamudPluginInterface _pluginInterface;
    private readonly ICommandManager _commandManager;
    private readonly IFramework _framework;
    private readonly Configuration _configuration;
    private readonly WindowSystem _windows = new(ProductIdentity.InternalName);
    private readonly INewFolder3AccessGate _accessGate;
    private readonly CommunityEvidenceCollector? _communityEvidenceCollector;
    private readonly FsdApplication _application;
    private readonly FsdWindow _window;
    private bool _disposed;

    public string Name => ProductIdentity.DisplayName;

    public Plugin(
        IDalamudPluginInterface pluginInterface,
        ICommandManager commandManager,
        IFramework framework,
        IPluginLog pluginLog)
    {
        _pluginInterface = pluginInterface ?? throw new ArgumentNullException(nameof(pluginInterface));
        _commandManager = commandManager ?? throw new ArgumentNullException(nameof(commandManager));
        _framework = framework ?? throw new ArgumentNullException(nameof(framework));

        FsdApplication? application = null;
        CommunityEvidenceCollector? communityEvidenceCollector = null;
        var omenInitialized = false;
        var commandRegistered = false;
        var drawSubscribed = false;
        var configSubscribed = false;
        var frameworkSubscribed = false;

        try
        {
            // ItemSourceManager is unrelated to FSD and starts a raw background build that
            // cannot be cancelled during constructor rollback.
            DService.Init(
                pluginInterface,
                static () => new DServiceInitOptions()
                    .Disable<ItemSourceManager>()
                    .Disable<LogMessageManager>()
                    .Disable<TargetManager>()
                    .Disable<UseActionManager>());
            omenInitialized = true;

            _configuration =
                pluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
            _configuration.Initialize(pluginInterface);
            _accessGate = NewFolder3BuildProfile.CreateAccessGate(pluginInterface, pluginLog);

            NewFolder3BuildCapabilities capabilities = NewFolder3BuildProfile.Capabilities;
            if (capabilities.CommunityEvidenceEndpoint is { } evidenceEndpoint)
            {
                communityEvidenceCollector = new CommunityEvidenceCollector(
                    pluginInterface.GetPluginConfigDirectory(),
                    _configuration.GetOrCreateCommunityEvidenceInstallationToken(),
                    evidenceEndpoint,
                    pluginLog);
            }

            _communityEvidenceCollector = communityEvidenceCollector;

            // FsdApplication injects the shared Dalamud services required by FSD.
            application = new FsdApplication(
                pluginInterface,
                _configuration,
                ProductIdentity.HostIdentity,
                typeof(Plugin).Assembly.GetName().Version?.ToString()
                    ?? throw new InvalidOperationException("Standalone host version is unavailable."),
                NewFolder3BuildProfile.CreateDetailedMapHostOptions(),
                _communityEvidenceCollector,
                tryAuthorizeFsdStart: _accessGate.TryAuthorizeFsdStart,
                fsdStartDenialNoticeProvider: () => _accessGate.FsdStartDenialNotice);
            _application = application;

            _window = new FsdWindow(_application);
            _windows.AddWindow(_window);

            _commandManager.AddHandler(ProductIdentity.Command, new CommandInfo(OnCommand)
            {
                HelpMessage = $"Open {ProductIdentity.DisplayName} Deep Dungeon FSD."
            });
            commandRegistered = true;

            _pluginInterface.UiBuilder.Draw += _windows.Draw;
            drawSubscribed = true;
            _pluginInterface.UiBuilder.OpenConfigUi += OpenWindow;
            configSubscribed = true;
            _framework.Update += OnFrameworkUpdate;
            frameworkSubscribed = true;
        }
        catch (Exception startupError)
        {
            var rollbackErrors = new List<Exception>();
            RollBack(() => { if (frameworkSubscribed) _framework.Update -= OnFrameworkUpdate; }, rollbackErrors);
            RollBack(() => { if (configSubscribed) _pluginInterface.UiBuilder.OpenConfigUi -= OpenWindow; }, rollbackErrors);
            RollBack(() => { if (drawSubscribed) _pluginInterface.UiBuilder.Draw -= _windows.Draw; }, rollbackErrors);
            RollBack(() => { if (commandRegistered) _commandManager.RemoveHandler(ProductIdentity.Command); }, rollbackErrors);
            RollBack(_windows.RemoveAllWindows, rollbackErrors);
            RollBack(() => application?.Dispose(), rollbackErrors);
            RollBack(() => communityEvidenceCollector?.Dispose(), rollbackErrors);
            RollBack(() => { if (omenInitialized) DService.Uninit(); }, rollbackErrors);

            if (rollbackErrors.Count == 0)
                throw;

            rollbackErrors.Insert(0, startupError);
            throw new AggregateException("Standalone FSD startup and rollback both failed.", rollbackErrors);
        }
    }

    private static void RollBack(Action action, List<Exception> errors)
    {
        try
        {
            action();
        }
        catch (Exception error)
        {
            errors.Add(error);
        }
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        _application.Update(framework);
        _communityEvidenceCollector?.ObserveRunState(
            _application.IsRunActive,
            _configuration.Fsd.UseDetailedMap,
            _application.ActiveDetailedMapReleaseId);
    }

    private void OnCommand(string command, string arguments) => _window.Toggle();
    private void OpenWindow() => _window.IsOpen = true;

    public void Dispose()
    {
        if (_disposed)
            return;
        _framework.Update -= OnFrameworkUpdate;
        _pluginInterface.UiBuilder.OpenConfigUi -= OpenWindow;
        _pluginInterface.UiBuilder.Draw -= _windows.Draw;
        _commandManager.RemoveHandler(ProductIdentity.Command);
        _windows.RemoveAllWindows();
        _application.Dispose();
        _communityEvidenceCollector?.Dispose();
        DService.Uninit();
        _disposed = true;
    }
}
