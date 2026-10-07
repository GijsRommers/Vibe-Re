using VibeRe.Domain.Provider;

namespace VibeRe.Application.Providers.Profiles;

public interface IProfileRepository
{
    Task<ProviderProfile?> FindAsync(string provider, CancellationToken cancellationToken = default);
    Task SaveAsync(ProviderProfile profile, CancellationToken cancellationToken = default);
}
