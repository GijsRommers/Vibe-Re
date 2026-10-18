namespace VibeRe.Client.Infrastructure.Configuration;

public sealed class ServerConnector(
    IClientConfigurationStore configurations,
    IServerTokenStore tokens,
    ILocalServerLauncher launcher) : IServerConnector
{
    public async Task<(ServerConnection Server, IVibeReClient Client)> ConnectAsync(string? name = null, string? tokenOverride = null,
        CancellationToken cancellationToken = default)
    {
        var configuration = await configurations.LoadAsync(cancellationToken);
        var server = configuration.Find(name);
        var token = tokenOverride ?? (server.HasAccessToken ? await tokens.GetAsync(server.Id, cancellationToken) : null);
        if (server.HasAccessToken && token is null)
            throw new InvalidOperationException("Saved server access token is unavailable. Set it again.");

        if (configuration.ServerRequired) await launcher.EnsureRunningAsync(server, cancellationToken);
        return (server, new VibeReClient(server.Address, token));
    }
}
