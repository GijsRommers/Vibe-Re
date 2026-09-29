using Latchkey;
using VibeRe.Application.Auth;

namespace VibeRe.Infrastructure.Auth.Secrets;

public sealed class LatchkeySecretStore(ILatchkey store): ISecretStore
{
    public async Task DeleteAsync(string key, CancellationToken cancellationToken)
    {
        await store.DeleteAsync(key, cancellationToken);
    }

    public async Task<string?> GetAsync(string key, CancellationToken cancellationToken)
    {
        return await store.GetAsync(key, cancellationToken);
    }

    public async Task SetAsync(string key, string value, CancellationToken cancellationToken)
    {
        await store.SetAsync(key, value, cancellationToken);
    }
}