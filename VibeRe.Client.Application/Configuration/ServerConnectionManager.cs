namespace VibeRe.Client.Application.Configuration;

public sealed class ServerConnectionManager(IClientConfigurationStore configurations, IServerTokenStore tokens)
    : IServerConnectionManager
{
    public Task<ClientConfiguration> LoadAsync(CancellationToken cancellationToken = default) => configurations.LoadAsync(cancellationToken);

    public Task<ClientConfiguration> AddAsync(string name, string address, CancellationToken cancellationToken = default, bool startLocally = false)
    {
        name = ServerConnection.ValidateName(name);
        address = ServerAddress.Normalize(address);
        if (startLocally && !ServerAddress.IsLoopback(address)) throw new ArgumentException("Only loopback servers can be started locally.");
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

    public async Task<ClientConfiguration> RemoveAsync(string name, CancellationToken cancellationToken = default)
    {
        Guid? obsoleteTokenId = null;
        var configuration = await configurations.UpdateAsync(config =>
        {
            var server = config.Find(name);
            if (server.HasAccessToken) obsoleteTokenId = server.Id;
            var remaining = config.Servers.Where(x => x.Id != server.Id).ToList();
            return Task.FromResult(config with
            {
                Servers = remaining,
                ActiveServer = string.Equals(config.ActiveServer, server.Name, StringComparison.OrdinalIgnoreCase)
                    ? remaining.FirstOrDefault()?.Name : config.ActiveServer
            });
        }, cancellationToken);
        await CleanupTokenAsync(obsoleteTokenId);
        return configuration;
    }

    public async Task<ClientConfiguration> UpdateAddressAsync(string name, string address, CancellationToken cancellationToken = default)
    {
        address = ServerAddress.Normalize(address);
        Guid? obsoleteTokenId = null;
        var configuration = await configurations.UpdateAsync(config =>
        {
            var server = config.Find(name);
            if (server.Address == address) return Task.FromResult(config);
            if (server.HasAccessToken) obsoleteTokenId = server.Id;
            // Commit a fresh identity before removing the old destination's token.
            return Task.FromResult(config with { Servers = config.Servers.Select(x => x.Id == server.Id
                ? x with { Id = Guid.NewGuid(), Address = address, HasAccessToken = false,
                    StartLocally = x.StartLocally && ServerAddress.IsLoopback(address) } : x).ToList() });
        }, cancellationToken);
        await CleanupTokenAsync(obsoleteTokenId);
        return configuration;
    }

    public Task<ClientConfiguration> SetServerRequiredAsync(bool required, CancellationToken cancellationToken = default) =>
        configurations.UpdateAsync(config => Task.FromResult(config with { ServerRequired = required }), cancellationToken);

    public Task<ClientConfiguration> SetLocalStartupAsync(string name, bool enabled, CancellationToken cancellationToken = default) =>
        configurations.UpdateAsync(config =>
        {
            var server = config.Find(name);
            if (enabled && !ServerAddress.IsLoopback(server.Address))
                throw new ArgumentException("Only loopback servers can be started locally.");
            return Task.FromResult(config with { Servers = config.Servers.Select(x => x.Id == server.Id
                ? x with { StartLocally = enabled } : x).ToList() });
        }, cancellationToken);

    public async Task<ClientConfiguration> SetTokenAsync(ServerConnection expectedServer, string token,
        CancellationToken cancellationToken = default)
    {
        Guid? newTokenId = null;
        Guid? obsoleteTokenId = null;
        ClientConfiguration configuration;
        try
        {
            configuration = await configurations.UpdateAsync(async config =>
            {
                // Check the displayed/prompted destination inside the write transaction, before
                // touching credentials. Both identity and origin must match the original snapshot.
                var server = config.Servers.FirstOrDefault(x => x.Id == expectedServer.Id);
                if (server is null || ServerAddress.Normalize(server.Address) != ServerAddress.Normalize(expectedServer.Address))
                    throw new ServerConnectionException("The server settings changed. Refresh them and enter the token or pair this device again.");
                cancellationToken.ThrowIfCancellationRequested();
                token = token.Trim();
                if (token.Any(c => c < 33 || c > 126))
                    throw new ArgumentException("Enter a valid server access token.");

                var replacementId = Guid.NewGuid();
                if (token.Length != 0)
                {
                    // Never overwrite a referenced secret before the database commits.
                    newTokenId = replacementId;
                    await tokens.SetAsync(replacementId, token, cancellationToken);
                }
                if (server.HasAccessToken) obsoleteTokenId = server.Id;
                return config with { Servers = config.Servers.Select(x => x.Id == server.Id
                    ? x with { Id = replacementId, HasAccessToken = token.Length != 0 } : x).ToList() };
            }, cancellationToken);
        }
        catch
        {
            await CleanupUncommittedTokenAsync(newTokenId);
            throw;
        }

        await CleanupTokenAsync(obsoleteTokenId);
        return configuration;
    }

    private async Task CleanupUncommittedTokenAsync(Guid? id)
    {
        if (id is null) return;
        using var check = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try
        {
            // A commit may have succeeded before its error reached us. Only delete a unique
            // replacement after confirming that no saved profile references it.
            var current = await configurations.LoadAsync(check.Token).WaitAsync(check.Token);
            if (current.Servers.All(server => server.Id != id.Value))
                await CleanupTokenAsync(id);
        }
        catch
        {
            // Uncertain persistence is safer than deleting a possibly referenced credential.
        }
    }

    private async Task CleanupTokenAsync(Guid? id)
    {
        if (id is null) return;
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try
        {
            await tokens.DeleteAsync(id.Value, cleanup.Token).WaitAsync(cleanup.Token);
        }
        catch
        {
            // Cleanup can leave an unreferenced keyring entry, but must not hide a committed
            // setting or replace the original write/cancellation error during rollback.
        }
    }
}
