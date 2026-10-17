using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using VibeRe.Client.Infrastructure.Adapters;

namespace VibeRe.Client.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddVibeReClient(this IServiceCollection services, string databasePath)
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
