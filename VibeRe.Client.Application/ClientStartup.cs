using VibeRe.Client.Application.Configuration;

namespace VibeRe.Client.Application;

// The same boot sequence for CLI and GUI. Platforms only decide how to present login.
public sealed class ClientStartup(IClientConfigurationStore configurations, IServerConnector connector)
{
    public async Task<ClientStartupResult> StartAsync(string? server = null, string? token = null,
        CancellationToken cancellationToken = default)
    {
        var configuration = await configurations.LoadAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (!configuration.ServerRequired)
            return new ServerOptional();

        var connection = await connector.ConnectAsync(server, token, cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var status = await connection.Client.GetStatusAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return new ServerSession(connection.Server, connection.Client, status);
        }
        catch
        {
            await connection.Client.DisposeAsync();
            throw;
        }
    }
}

public abstract record ClientStartupResult
{
    private protected ClientStartupResult() { }
}

public sealed record ServerOptional : ClientStartupResult;

// A session always contains the server's actual initial authentication status.
public sealed record ServerSession(ServerConnection Server, IVibeReClient Client, AuthStatus Status)
    : ClientStartupResult, IAsyncDisposable
{
    public ValueTask DisposeAsync() => Client.DisposeAsync();
}
