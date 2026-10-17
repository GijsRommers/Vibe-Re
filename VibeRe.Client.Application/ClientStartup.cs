using VibeRe.Client.Application.Configuration;

namespace VibeRe.Client.Application;

// The same boot sequence for CLI and GUI. Platforms only decide how to present login.
public sealed class ClientStartup(IClientConfigurationStore configurations, IServerConnector connector)
{
    public async Task<ServerSession?> StartAsync(string? server = null, string? token = null,
        CancellationToken cancellationToken = default)
    {
        if (!(await configurations.LoadAsync(cancellationToken)).ServerRequired) return null;
        var session = await connector.ConnectAsync(server, token, cancellationToken);
        try { return session with { Status = await session.Client.GetStatusAsync(cancellationToken) }; }
        catch { session.Dispose(); throw; }
    }
}

public sealed record ServerSession(ServerConnection Server, IVibeReClient Client, AuthStatus? Status = null) : IDisposable
{
    public void Dispose() => Client.Dispose();
}
