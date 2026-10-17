namespace VibeRe.Client.Configuration;

public interface IServerConnector
{
    Task<IVibeReClient> ConnectAsync(string? name = null, string? tokenOverride = null,
        CancellationToken cancellationToken = default);
}
