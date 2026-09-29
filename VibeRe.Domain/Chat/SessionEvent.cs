using VibeRe.Domain.Common;

namespace VibeRe.Domain.Chat;

public class SessionEvent : TimeStampedEntity
{
    public Guid Id { get; set; }

    public Session Session { get; set; }
}