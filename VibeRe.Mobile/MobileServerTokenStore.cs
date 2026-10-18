using VibeRe.Client.Application.Configuration;

namespace VibeRe.Mobile;

public sealed class MobileServerTokenStore : IServerTokenStore
{
    public async Task<string?> GetAsync(Guid serverId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await SecureStorage.Default.GetAsync(Key(serverId));
    }
    public async Task SetAsync(Guid serverId, string token, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await SecureStorage.Default.SetAsync(Key(serverId), token);
    }
    public Task DeleteAsync(Guid serverId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SecureStorage.Default.Remove(Key(serverId));
        return Task.CompletedTask;
    }
    private static string Key(Guid id) => $"vibere-server-access-{id:D}";
}
