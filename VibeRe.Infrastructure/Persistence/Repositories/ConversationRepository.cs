using Microsoft.EntityFrameworkCore;
using VibeRe.Application.Conversations;
using VibeRe.Domain.Conversations;

namespace VibeRe.Infrastructure.Persistence.Repositories;

public sealed class ConversationRepository(VibeReDbContext db, TimeProvider? time = null) : IConversationRepository
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public Task<Conversation?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        db.Conversations.AsNoTracking().SingleOrDefaultAsync(conversation => conversation.Id == id, cancellationToken);

    public async Task<IReadOnlyList<Conversation>> ListAsync(
        ConversationStatus status = ConversationStatus.Active,
        CancellationToken cancellationToken = default) =>
        await db.Conversations
            .AsNoTracking()
            .Where(conversation => conversation.Status == status)
            .OrderByDescending(conversation => conversation.CreatedAt)
            .ToListAsync(cancellationToken);

    public async Task AddAsync(Conversation conversation, CancellationToken cancellationToken = default)
    {
        db.Conversations.Add(conversation);
        await db.SaveChangesAsync(cancellationToken);
    }

    public Task<bool> FinishAsync(Guid id, CancellationToken cancellationToken = default) =>
        UpdateAsync(id, conversation => conversation.Finish(_time.GetUtcNow().UtcDateTime), cancellationToken);

    public Task<bool> ReopenAsync(Guid id, CancellationToken cancellationToken = default) =>
        UpdateAsync(id, conversation => conversation.Reopen(), cancellationToken);

    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var conversation = await db.Conversations.FindAsync([id], cancellationToken);
        if (conversation is null)
        {
            return false;
        }

        db.Conversations.Remove(conversation);
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    private async Task<bool> UpdateAsync(Guid id, Action<Conversation> change, CancellationToken cancellationToken)
    {
        var conversation = await db.Conversations.FindAsync([id], cancellationToken);
        if (conversation is null)
        {
            return false;
        }

        change(conversation);
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }
}
