namespace VibeRe.Runtime.Configuration;

public interface IServerTokenStore
{
    Task<string?> GetAsync(Guid serverId, CancellationToken cancellationToken = default);
    Task SetAsync(Guid serverId, string token, CancellationToken cancellationToken = default);
    Task DeleteAsync(Guid serverId, CancellationToken cancellationToken = default);
}
