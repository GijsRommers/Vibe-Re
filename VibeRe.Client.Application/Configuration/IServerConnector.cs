namespace VibeRe.Client.Application.Configuration;

public interface IServerConnector
{
    // The caller owns the client; connecting does not query provider authentication.
    Task<(ServerConnection Server, IVibeReClient Client)> ConnectAsync(string? name = null, string? tokenOverride = null,
        CancellationToken cancellationToken = default);
}
