namespace VibeRe.Client.Configuration;

public sealed class ServerConnectionManager(ClientConnectionDatabase configurations, IServerTokenStore tokens)
{
    public static ServerConnectionManager CreateDesktop() =>
        new(new(ClientConnectionDatabase.DesktopPath), new KeyringServerTokenStore());

    public Task<ClientConfiguration> LoadAsync(CancellationToken cancellationToken = default) => configurations.LoadAsync(cancellationToken);

    public Task<ClientConfiguration> AddAsync(string name, string address, CancellationToken cancellationToken = default, bool startLocally = false)
    {
        name = ServerConnection.ValidateName(name);
        address = ServerConnection.NormalizeAddress(address);
        if (startLocally && !new Uri(address).IsLoopback) throw new ArgumentException("Only loopback servers can be started locally.");
        return configurations.UpdateAsync(config =>
        {
            if (config.Servers.Any(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException("A server with that name already exists.");
            return Task.FromResult(config with
            {
                ActiveServer = config.ActiveServer ?? name,
                Servers = [.. config.Servers, new(Guid.NewGuid(), name, address, StartLocally: startLocally)]
            });
        }, cancellationToken);
    }

    public Task<ClientConfiguration> SelectAsync(string name, CancellationToken cancellationToken = default) =>
        configurations.UpdateAsync(config => Task.FromResult(config with { ActiveServer = config.Find(name).Name }), cancellationToken);

    public Task<ClientConfiguration> RemoveAsync(string name, CancellationToken cancellationToken = default) =>
        configurations.UpdateAsync(async config =>
        {
            var server = config.Find(name);
            if (server.HasAccessToken) await tokens.DeleteAsync(server.Id, cancellationToken);
            var remaining = config.Servers.Where(x => x.Id != server.Id).ToList();
            return config with
            {
                Servers = remaining,
                ActiveServer = string.Equals(config.ActiveServer, server.Name, StringComparison.OrdinalIgnoreCase)
                    ? remaining.FirstOrDefault()?.Name : config.ActiveServer
            };
        }, cancellationToken);

    public Task<ClientConfiguration> UpdateAddressAsync(string name, string address, CancellationToken cancellationToken = default)
    {
        address = ServerConnection.NormalizeAddress(address);
        return configurations.UpdateAsync(async config =>
        {
            var server = config.Find(name);
            if (server.Address == address) return config;
            // A saved access token is scoped to its destination and never follows an address change.
            if (server.HasAccessToken) await tokens.DeleteAsync(server.Id, cancellationToken);
            return config with { Servers = config.Servers.Select(x => x.Id == server.Id
                ? x with { Address = address, HasAccessToken = false, StartLocally = x.StartLocally && new Uri(address).IsLoopback } : x).ToList() };
        }, cancellationToken);
    }

    public Task<ClientConfiguration> SetLocalStartupAsync(string name, bool enabled, CancellationToken cancellationToken = default) =>
        configurations.UpdateAsync(config =>
        {
            var server = config.Find(name);
            if (enabled && !new Uri(server.Address).IsLoopback)
                throw new ArgumentException("Only loopback servers can be started locally.");
            return Task.FromResult(config with { Servers = config.Servers.Select(x => x.Id == server.Id
                ? x with { StartLocally = enabled } : x).ToList() });
        }, cancellationToken);

    public Task<ClientConfiguration> SetTokenAsync(string name, string token, CancellationToken cancellationToken = default) =>
        configurations.UpdateAsync(async config =>
        {
            var server = config.Find(name);
            token = token.Trim();
            if (token.Any(c => c < 33 || c > 126)) throw new ArgumentException("Enter a valid server access token.");
            if (token.Length == 0)
            {
                if (server.HasAccessToken) await tokens.DeleteAsync(server.Id, cancellationToken);
            }
            else await tokens.SetAsync(server.Id, token, cancellationToken);
            return config with { Servers = config.Servers.Select(x => x.Id == server.Id
                ? x with { HasAccessToken = token.Length != 0 } : x).ToList() };
        }, cancellationToken);

    public async Task<VibeReClient> ConnectAsync(string? name = null, string? tokenOverride = null,
        CancellationToken cancellationToken = default)
    {
        var server = (await configurations.LoadAsync(cancellationToken)).Find(name);
        var token = tokenOverride ?? (server.HasAccessToken ? await tokens.GetAsync(server.Id, cancellationToken) : null);
        if (server.HasAccessToken && token is null) throw new InvalidOperationException("Saved server access token is unavailable. Set it again.");
        await new LocalServerLauncher().EnsureRunningAsync(server, cancellationToken);
        return new(server, token);
    }
}
