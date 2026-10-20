using Latchkey.Extensions.DependencyInjection;
using VibeRe.Server.Application.Workspace;
using VibeRe.Server.Infrastructure.Workspace;
using VibeRe.Server.Application.Access;
using VibeRe.Server.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VibeRe.Server.Application.Secrets;
using VibeRe.Server.Infrastructure.Persistence;
using VibeRe.Server.Infrastructure.Persistence.Interceptors;
using VibeRe.Server.Infrastructure.Secrets;
using VibeRe.Server.Application.Providers.Auth;
using VibeRe.Server.Application.Providers.Profiles;
using VibeRe.Server.Infrastructure.Providers.Mistral.Auth;

namespace VibeRe.Server;

internal static class DependencyInjection
{
    public static IServiceCollection AddVibeReServices(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IDeviceSessionStore, DeviceSessionStore>();
        services.AddSingleton<DeviceAccessService>();
        services.AddSingleton<IDeviceAccessService>(sp => sp.GetRequiredService<DeviceAccessService>());
        services.AddSingleton<IDeviceSessionIssuer>(sp => sp.GetRequiredService<DeviceAccessService>());
        services.AddSingleton<ClientAccessPolicy>();
        services.AddSingleton<DeviceConnectionGuard>();
        services.AddSingleton<DeviceAccessHubFilter>();
        services.AddSingleton<TimestampedEntityInterceptor>();

        services.AddDbContextFactory<VibeReDbContext>((sp, options) =>
        {
            options.UseSqlite(configuration.GetConnectionString("VibeRe") ?? DatabaseConnectionString());
            options.AddInterceptors(sp.GetRequiredService<TimestampedEntityInterceptor>());
        });

        services.AddLatchkey("dev.vibe-re");
        services.AddSingleton<ISecretStore, LatchkeySecretStore>();
        services.AddSingleton<IProfileRepository, ProfileRepository>();
        services.AddSingleton<IProviderCredentialStore, ProviderCredentialStore>();
        services.AddSingleton<ICredentialResolver>(sp => sp.GetRequiredService<IProviderCredentialStore>());
        services.AddHttpClient<MistralAuthFlow>(client => client.Timeout = TimeSpan.FromSeconds(20))
            .RemoveAllLoggers()
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        services.AddHttpClient<MistralBrowserAuthFlow>(client => client.Timeout = TimeSpan.FromSeconds(20))
            .RemoveAllLoggers()
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        services.AddSingleton<IAuthFlow>(sp => sp.GetRequiredService<MistralAuthFlow>());
        services.AddSingleton<IBrowserAuthFlow>(sp => sp.GetRequiredService<MistralBrowserAuthFlow>());
        services.AddSingleton<IAuthService, AuthService>();
        services.AddSingleton<IBrowserAuthService, BrowserAuthService>();

        services.AddSingleton(new WorkspaceOptions(
            WorkspaceRoot(configuration["Workspace:Root"])));
        services.AddSingleton<IWorkspaceStore, WorkspaceStore>();
        services.AddHttpClient("cockpit", client => client.Timeout = TimeSpan.FromSeconds(90))
            .RemoveAllLoggers().ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        services.AddSingleton<IAgentModel>(sp =>
            new MistralAgentModel(
                sp.GetRequiredService<IHttpClientFactory>().CreateClient("cockpit"), sp.GetRequiredService<ICredentialResolver>(),
                configuration["Workspace:Model"] ?? "devstral-latest"));
        services.AddSingleton<IWorkspaceTools, WorkspaceTools>();
        services.AddSingleton<ICodingAgent, CodingHarness>();
        services.AddSingleton<WorkspaceService>();

        return services;
    }

    private static string WorkspaceRoot(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured)) return Path.GetFullPath(configured);
        for (var directory = new DirectoryInfo(Directory.GetCurrentDirectory()); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, ".git")) || Directory.Exists(Path.Combine(directory.FullName, ".git")))
                return directory.FullName;
        return Directory.GetCurrentDirectory();
    }

    // The database always lives in ~/.vibe-re; SQLite won't create the folder itself.
    private static string DatabaseConnectionString()
    {
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".vibe-re");
        Directory.CreateDirectory(directory);

        return new SqliteConnectionStringBuilder { DataSource = Path.Combine(directory, "vibe-re.db") }.ToString();
    }
}
