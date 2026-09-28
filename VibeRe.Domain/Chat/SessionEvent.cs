namespace VibeRe.Domain.Chat;

public class SessionEvent
{
    public Guid Id { get; set; }

    public Session Session { get; set; }
}