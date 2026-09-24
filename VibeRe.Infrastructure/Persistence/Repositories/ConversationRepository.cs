using Microsoft.EntityFrameworkCore;
using VibeRe.Application.Conversations;
using VibeRe.Domain.Conversations;

namespace VibeRe.Infrastructure.Persistence.Repositories;

public sealed class ConversationRepository(VibeReDbContext db) : IConversationRepository
{
    public Task<Conversation?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        db.Conversations.AsNoTracking().SingleOrDefaultAsync(conversation => conversation.Id == id, cancellationToken);

    public async Task AddAsync(Conversation conversation, CancellationToken cancellationToken = default)
    {
        db.Conversations.Add(conversation);
        await db.SaveChangesAsync(cancellationToken);
    }

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
}
