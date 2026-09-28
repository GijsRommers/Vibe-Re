namespace VibeRe.Domain.Chat;

public class Session
{
    public Guid Id { get; set; }

    public Conversation Conversation { get; set; }
}