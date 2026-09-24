using VibeRe.Domain.Conversations;

namespace VibeRe.Application.Conversations;

public interface IConversationRepository
{
    Task<Conversation?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Conversations with the given status, newest first. Defaults to the active ones the user is working on.</summary>
    Task<IReadOnlyList<Conversation>> ListAsync(
        ConversationStatus status = ConversationStatus.Active,
        CancellationToken cancellationToken = default);

    Task AddAsync(Conversation conversation, CancellationToken cancellationToken = default);

    /// <summary>Marks the task done; false if it doesn't exist. Finishing twice keeps the first finish time.</summary>
    Task<bool> FinishAsync(Guid id, CancellationToken cancellationToken = default);

    Task<bool> ReopenAsync(Guid id, CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}
