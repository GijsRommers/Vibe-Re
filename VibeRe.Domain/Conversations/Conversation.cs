namespace VibeRe.Domain.Conversations;

public enum ConversationStatus
{
    Active,
    Finished,
}

/// <summary>
/// A task the user gave the agent. Finishing it takes it off the active list without losing its
/// history, and it can be reopened later.
/// </summary>
public sealed class Conversation
{
    private Conversation() { }

    public Conversation(string title)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        Title = title.Trim();
    }

    public Guid Id { get; private set; } = Guid.NewGuid();

    public string Title { get; private set; } = null!;

    public ConversationStatus Status { get; private set; } = ConversationStatus.Active;

    public DateTime CreatedAt { get; private set; } = DateTime.UtcNow;

    public DateTime? FinishedAt { get; private set; }

    public void Finish(DateTime finishedAt)
    {
        if (Status == ConversationStatus.Finished)
        {
            return;
        }

        Status = ConversationStatus.Finished;
        FinishedAt = finishedAt;
    }

    public void Reopen()
    {
        Status = ConversationStatus.Active;
        FinishedAt = null;
    }
}
