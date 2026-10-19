using Microsoft.Extensions.DependencyInjection;
using VibeRe.Client.Application.Configuration;
using VibeRe.Client.Application;
using VibeRe.Client.Infrastructure;

namespace VibeRe.Client.Tests;

public class ArchitectureTests
{
    [Fact]
    public void ApplicationDependsOnlyOnRuntimeLibrariesAndPresentationToolkit()
    {
        var dependencies = typeof(ClientStartup).Assembly.GetReferencedAssemblies();
        Assert.All(dependencies, dependency => Assert.True(
            dependency.Name!.StartsWith("System.", StringComparison.Ordinal) ||
            dependency.Name == "CommunityToolkit.Mvvm", $"Unexpected application dependency: {dependency.Name}"));
        Assert.Contains(typeof(VibeReClient).Assembly.GetReferencedAssemblies(),
            dependency => dependency.Name == typeof(ClientStartup).Assembly.GetName().Name);
    }
    [Fact]
    public void HostsCanSupplyNativeBrowserAndTokenStoreBeforeComposition()
    {
        var browser = new Browser();
        var tokens = new Tokens();
        using var services = new ServiceCollection()
            .AddSingleton<IBrowserLauncher>(browser)
            .AddSingleton<IServerTokenStore>(tokens)
            .AddVibeReClient(Path.Combine(Path.GetTempPath(), "unused-client.db"))
            .BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });

        Assert.Same(browser, services.GetRequiredService<IBrowserLauncher>());
        Assert.Same(tokens, services.GetRequiredService<IServerTokenStore>());
        Assert.NotNull(services.GetRequiredService<ConnectionSettingsViewModel>());
    }

    private sealed class Browser : IBrowserLauncher
    {
        public Task OpenAsync(Uri uri) => throw new NotSupportedException();
    }

    private sealed class Tokens : IServerTokenStore
    {
        public Task<string?> GetAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetAsync(Guid id, string token, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
