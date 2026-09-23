namespace VibeRe.Domain.Conversations;

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
}
