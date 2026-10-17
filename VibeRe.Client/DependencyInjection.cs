using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using VibeRe.Client.Configuration;

namespace VibeRe.Client;

public static class DependencyInjection
{
    public static IServiceCollection AddVibeReClient(this IServiceCollection services, string databasePath)
    {
        services.AddSingleton<IClientConfigurationStore>(new ClientConnectionDatabase(databasePath));
        services.TryAddSingleton<IServerTokenStore, KeyringServerTokenStore>();
        services.TryAddSingleton<IBrowserLauncher, SystemBrowserLauncher>();
        services.AddSingleton<IServerConnectionManager, ServerConnectionManager>();
        services.AddSingleton<IServerConnector, ServerConnector>();
        services.AddSingleton<IServerReadinessProbe, ServerReadinessProbe>();
        services.AddSingleton<ILocalServerProcess, LocalServerProcess>();
        services.AddSingleton<ILocalServerLauncher, LocalServerLauncher>();
        services.AddTransient<LocalServerMonitor>();
        services.AddTransient<ConnectionSettingsViewModel>();
        return services;
    }
}
