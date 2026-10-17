using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using VibeRe.Runtime.Adapters;

namespace VibeRe.Runtime;

public static class DependencyInjection
{
    public static IServiceCollection AddVibeReRuntime(this IServiceCollection services, string databasePath)
    {
        services.AddSingleton<IClientConfigurationStore>(new ClientConnectionDatabase(databasePath));
        services.TryAddSingleton<IServerTokenStore, KeyringServerTokenStore>();
        services.TryAddSingleton<IBrowserLauncher, SystemBrowserLauncher>();
        services.AddSingleton<IServerConnectionManager, ServerConnectionManager>();
        services.AddSingleton<IServerConnector, ServerConnector>();
        services.AddSingleton<ClientStartup>();
        services.AddSingleton<IServerReadinessProbe, ServerReadinessProbe>();
        services.AddSingleton<ILocalServerProcess, LocalServerProcess>();
        services.AddSingleton<ILocalServerLauncher, LocalServerLauncher>();
        services.AddTransient<ConnectionSettingsViewModel>();
        return services;
    }
}
