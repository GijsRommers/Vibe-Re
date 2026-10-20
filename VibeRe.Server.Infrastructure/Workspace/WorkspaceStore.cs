using Microsoft.EntityFrameworkCore;
using VibeRe.Domain.Chat;
using VibeRe.Server.Application.Workspace;
using VibeRe.Server.Infrastructure.Persistence;

namespace VibeRe.Server.Infrastructure.Workspace;

public sealed class WorkspaceStore(IDbContextFactory<VibeReDbContext> factory) : IWorkspaceStore
{
    public async Task<IReadOnlyList<WorkspaceThread>> ListAsync(CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.WorkspaceThreads.AsNoTracking().OrderByDescending(x => x.UpdatedAt).ToListAsync(ct);
    }
    public async Task<WorkspaceThread?> FindAsync(Guid id, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.WorkspaceThreads.AsNoTracking().Include(x => x.Messages.OrderBy(m => m.Sequence))
            .SingleOrDefaultAsync(x => x.Id == id, ct);
    }
    public async Task<bool> HasMessageAsync(Guid id, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.WorkspaceMessages.AnyAsync(x => x.Id == id, ct);
    }
    public Task SaveAsync(WorkspaceThread thread, CancellationToken ct) => CompleteAsync(thread, null, ct);
    public async Task CompleteAsync(WorkspaceThread thread, WorkspaceMessage? report, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var existing = await db.WorkspaceThreads.FindAsync([thread.Id], ct);
        if (existing is null) db.WorkspaceThreads.Add(thread);
        else
        {
            db.Entry(existing).CurrentValues.SetValues(thread);
            foreach (var message in thread.Messages)
            {
                var persisted = await db.WorkspaceMessages.FindAsync([message.Id], ct);
                if (persisted is null) db.WorkspaceMessages.Add(message);
                else if (message.Handled != persisted.Handled) persisted.Handled = message.Handled;
            }
        }
        if (report is not null && !await db.WorkspaceMessages.AnyAsync(x => x.Id == report.Id, ct))
        {
            report.Sequence = (await db.WorkspaceMessages.Where(x => x.ThreadId == report.ThreadId)
                .MaxAsync(x => (int?)x.Sequence, ct) ?? 0) + 1;
            db.WorkspaceMessages.Add(report);
        }
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }
}
