using System.Net.Http;
using MorrowindRemasteredLauncher.ViewModels;

namespace MorrowindRemasteredLauncher.Services;

/// <summary>Minimal hand-rolled service container that registers singletons and resolves them by type, kept lightweight to keep the single-file binary small.</summary>
public sealed class ServiceRegistry
{
    /// <summary>The registered singletons, keyed by type.</summary>
    private readonly Dictionary<Type, object> _instances = new();

    /// <summary>Constructs and registers every service and the shell view-model in dependency order; notably GamePathService must precede InstallStateService, which uses it to treat an embedded install as "installed" once a game path is chosen.</summary>
    public void Initialize()
    {
        var http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(100)
        };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("MorrowindRemasteredLauncher/0.1");
        Register(http);

        var config = new ConfigService();
        config.Load();
        Register(config);

        var environment = new EnvironmentService(config).Detect();
        Register(environment);

        Register(new ModlistCatalogService(http, config));
        Register(new GamePathService(config));
        Register(new InstallStateService(config, environment, Get<GamePathService>()));
        Register(new SteamService(config));
        Register(new WabbajackTokenStore());
        Register(new NexusAuthService(http, Get<WabbajackTokenStore>(), config));
        Register(new WabbajackCliService(http, config));
        Register(new InstallEngine(
            Get<WabbajackCliService>(),
            Get<NexusAuthService>(),
            Get<InstallStateService>(),
            config));

        Register(new DisplayService());
        Register(new Mo2IniService(config, Get<GamePathService>(), Get<InstallStateService>()));
        Register(new PostSetupConfigService(
            config, Get<InstallStateService>(), Get<GamePathService>(), Get<DisplayService>()));
        Register(new BinarySetupService(http, config, Get<InstallStateService>()));
        Register(new PostSetupVerifier(
            config, Get<InstallStateService>(), Get<GamePathService>(), Get<SteamService>()));
        Register(new GameSettingsService(
            config, Get<InstallStateService>(), Get<GamePathService>(), Get<DisplayService>()));
        Register(new Mo2LaunchService(Get<InstallStateService>(), config));
        Register(new Mo2ToolAutomation(Get<Mo2LaunchService>(), Get<InstallStateService>(), config));
        Register(new PostSetupService(
            config,
            Get<InstallStateService>(),
            Get<PostSetupVerifier>(),
            Get<Mo2IniService>(),
            Get<PostSetupConfigService>(),
            Get<BinarySetupService>(),
            Get<Mo2ToolAutomation>(),
            Get<SteamService>()));

        Register(new ShellViewModel(
            Get<ConfigService>(),
            Get<ModlistCatalogService>(),
            Get<InstallStateService>(),
            Get<GamePathService>(),
            Get<NexusAuthService>(),
            Get<InstallEngine>(),
            Get<PostSetupService>(),
            Get<PostSetupVerifier>(),
            Get<PostSetupConfigService>(),
            Get<DisplayService>(),
            Get<Mo2LaunchService>(),
            Get<GameSettingsService>(),
            Get<SteamService>(),
            environment));
    }

    /// <summary>Registers a singleton under its type <typeparamref name="T"/>.</summary>
    public void Register<T>(T instance) where T : class
        => _instances[typeof(T)] = instance;

    /// <summary>Resolves the singleton of type <typeparamref name="T"/>, or throws if it isn't registered.</summary>
    public T Get<T>() where T : class
    {
        if (_instances.TryGetValue(typeof(T), out var instance))
        {
            return (T)instance;
        }
        throw new InvalidOperationException($"Service {typeof(T).Name} is not registered.");
    }
}
