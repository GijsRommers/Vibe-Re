using Microsoft.EntityFrameworkCore;
using VibeRe.Application.Conversations;
using VibeRe.Domain.Conversations;

namespace VibeRe.Infrastructure.Persistence.Repositories;

public sealed class ConversationEventStore(VibeReDbContext db) : IConversationEventStore
{
    public async Task<ConversationEvent> AppendAsync(
        Guid conversationId,
        long expectedNextSequence,
        string kind,
        string dataJson,
        Guid? turnId = null,
        Guid? stepId = null,
        Guid? nodeId = null,
        CancellationToken cancellationToken = default)
    {
        var entry = new ConversationEvent(conversationId, expectedNextSequence, kind, dataJson, turnId, stepId, nodeId);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var lastSequence = await db.ConversationEvents
            .Where(existing => existing.ConversationId == conversationId)
            .MaxAsync(existing => (long?)existing.Sequence, cancellationToken) ?? 0;
        if (expectedNextSequence != lastSequence + 1)
        {
            throw new InvalidOperationException(
                $"Expected event sequence {lastSequence + 1} for conversation {conversationId}.");
        }

        db.ConversationEvents.Add(entry);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return entry;
    }

    public async Task<IReadOnlyList<ConversationEvent>> ReadAsync(
        Guid conversationId,
        long afterSequence = 0,
        CancellationToken cancellationToken = default) =>
        await db.ConversationEvents
            .AsNoTracking()
            .Where(entry => entry.ConversationId == conversationId && entry.Sequence > afterSequence)
            .OrderBy(entry => entry.Sequence)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<ConversationEvent>> ReadTurnAsync(
        Guid conversationId,
        Guid turnId,
        CancellationToken cancellationToken = default) =>
        await db.ConversationEvents
            .AsNoTracking()
            .Where(entry => entry.ConversationId == conversationId && entry.TurnId == turnId)
            .OrderBy(entry => entry.Sequence)
            .ToListAsync(cancellationToken);
}
