using VibeRe.Domain.Common;

namespace VibeRe.Domain.Chat;

public class Conversation: TimeStampedEntity
{
    public Guid Id { get; set; }
}