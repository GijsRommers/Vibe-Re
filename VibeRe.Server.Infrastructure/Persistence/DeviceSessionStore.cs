using Microsoft.EntityFrameworkCore;
using VibeRe.Domain.Access;
using VibeRe.Server.Application.Access;

namespace VibeRe.Server.Infrastructure.Persistence;

public sealed class DeviceSessionStore(IDbContextFactory<VibeReDbContext> factory) : IDeviceSessionStore
{
    public async Task CreateAsync(DeviceSession session, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        db.DeviceSessions.Add(session);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<DeviceSession?> FindByTokenHashAsync(string tokenHash,
        CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        return await db.DeviceSessions.AsNoTracking()
            .SingleOrDefaultAsync(x => x.TokenHash == tokenHash, cancellationToken);
    }

    public async Task<IReadOnlyList<DeviceSession>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        return await db.DeviceSessions.AsNoTracking().ToArrayAsync(cancellationToken);
    }

    public async Task<bool> RevokeAsync(Guid deviceId, DateTimeOffset revokedAt,
        CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var changed = await db.DeviceSessions.Where(x => x.Id == deviceId && x.RevokedAt == null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.RevokedAt, revokedAt), cancellationToken);
        // Idempotent, preserving the first revocation timestamp; no stale entity can undo it.
        return changed > 0 || await db.DeviceSessions.AnyAsync(x => x.Id == deviceId, cancellationToken);
    }
}
