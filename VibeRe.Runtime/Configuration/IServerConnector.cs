namespace VibeRe.Runtime.Configuration;

public interface IServerConnector
{
    Task<ServerSession> ConnectAsync(string? name = null, string? tokenOverride = null,
        CancellationToken cancellationToken = default);
}
