using VibeRe.Domain.Conversations;

namespace VibeRe.Application.Conversations;

public interface IConversationEventStore
{
    Task<ConversationEvent> AppendAsync(
        Guid conversationId,
        long expectedNextSequence,
        string kind,
        string dataJson,
        Guid? turnId = null,
        Guid? stepId = null,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ConversationEvent>> ReadAsync(
        Guid conversationId,
        long afterSequence = 0,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ConversationEvent>> ReadTurnAsync(
        Guid conversationId,
        Guid turnId,
        CancellationToken cancellationToken = default);
}
