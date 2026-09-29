using VibeRe.Domain.Common;

namespace VibeRe.Domain.Chat;

public class Session : TimeStampedEntity
{
    public Guid Id { get; set; }

    public Conversation Conversation { get; set; }
}