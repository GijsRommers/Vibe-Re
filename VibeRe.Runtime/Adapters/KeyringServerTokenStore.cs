using Latchkey;

namespace VibeRe.Runtime.Adapters;

public sealed class KeyringServerTokenStore : IServerTokenStore
{
    private readonly Lazy<ILatchkey> _store = new(() => LatchkeyFactory.Create("dev.vibe-re.clients"));
    public async Task<string?> GetAsync(Guid serverId, CancellationToken cancellationToken = default) =>
        await _store.Value.GetAsync(Key(serverId), cancellationToken);
    public async Task SetAsync(Guid serverId, string token, CancellationToken cancellationToken = default) =>
        await _store.Value.SetAsync(Key(serverId), token, cancellationToken);
    public async Task DeleteAsync(Guid serverId, CancellationToken cancellationToken = default) =>
        await _store.Value.DeleteAsync(Key(serverId), cancellationToken);
    private static string Key(Guid id) => $"server-access/{id:D}";
}
