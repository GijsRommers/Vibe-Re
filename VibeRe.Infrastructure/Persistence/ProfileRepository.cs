using Microsoft.EntityFrameworkCore;
using VibeRe.Application.Providers.Profiles;
using VibeRe.Domain.Provider;

namespace VibeRe.Infrastructure.Persistence;

public sealed class ProfileRepository(IDbContextFactory<VibeReDbContext> factory) : IProfileRepository
{
    public async Task<ProviderProfile?> FindAsync(string provider, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        return await db.ProviderProfiles.AsNoTracking().Include(x => x.Credential)
            .SingleOrDefaultAsync(x => x.ProviderIdentifier == provider, cancellationToken);
    }

    public async Task SaveAsync(ProviderProfile profile, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var exists = await db.ProviderProfiles.AnyAsync(x => x.Id == profile.Id, cancellationToken);
        if (exists) db.Update(profile);
        else db.Add(profile);
        await db.SaveChangesAsync(cancellationToken);
    }
}
