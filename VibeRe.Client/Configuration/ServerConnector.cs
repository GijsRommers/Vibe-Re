namespace VibeRe.Client.Configuration;

public sealed class ServerConnector(
    IClientConfigurationStore configurations,
    IServerTokenStore tokens,
    ILocalServerLauncher launcher) : IServerConnector
{
    public async Task<IVibeReClient> ConnectAsync(string? name = null, string? tokenOverride = null,
        CancellationToken cancellationToken = default)
    {
        var server = (await configurations.LoadAsync(cancellationToken)).Find(name);
        var token = tokenOverride ?? (server.HasAccessToken ? await tokens.GetAsync(server.Id, cancellationToken) : null);
        if (server.HasAccessToken && token is null)
            throw new InvalidOperationException("Saved server access token is unavailable. Set it again.");

        await launcher.EnsureRunningAsync(server, cancellationToken);
        return new VibeReClient(server, token);
    }
}
