using Microsoft.EntityFrameworkCore;
using VibeRe.Application.Tools;
using VibeRe.Domain.Tools;

namespace VibeRe.Infrastructure.Persistence.Repositories;

public sealed class McpServerRepository(VibeReDbContext db) : IMcpServerRepository
{
    public async Task<IReadOnlyList<McpServer>> ListAsync(CancellationToken cancellationToken = default) =>
        await db.McpServers.AsNoTracking().OrderBy(server => server.Name).ToListAsync(cancellationToken);

    public Task<McpServer?> GetByNameAsync(string name, CancellationToken cancellationToken = default) =>
        db.McpServers.AsNoTracking().SingleOrDefaultAsync(server => server.Name == name, cancellationToken);

    public async Task AddAsync(McpServer server, CancellationToken cancellationToken = default)
    {
        var entry = db.McpServers.Add(server);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Most likely a duplicate name; don't let the rejected row poison later saves.
            entry.State = EntityState.Detached;
            throw;
        }
    }

    public async Task<bool> SetEnabledAsync(string name, bool enabled, CancellationToken cancellationToken = default)
    {
        var server = await db.McpServers.SingleOrDefaultAsync(existing => existing.Name == name, cancellationToken);
        if (server is null)
        {
            return false;
        }

        if (enabled)
        {
            server.Enable();
        }
        else
        {
            server.Disable();
        }

        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<bool> DeleteAsync(string name, CancellationToken cancellationToken = default) =>
        await db.McpServers.Where(server => server.Name == name).ExecuteDeleteAsync(cancellationToken) > 0;
}
