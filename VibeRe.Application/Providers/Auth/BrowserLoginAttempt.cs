namespace VibeRe.Application.Providers.Auth;

internal sealed class BrowserLoginAttempt(string provider, string verifier, BrowserProcess process, BrowserAttemptStatus status)
{
    public string Provider { get; } = provider;
    public string? Verifier { get; set; } = verifier;
    public BrowserProcess Process { get; } = process;
    public BrowserAttemptStatus Status { get; set; } = status;
    public DateTimeOffset NextPollAt { get; set; }
    public SemaphoreSlim Gate { get; } = new(1, 1);

    public BrowserAttemptStatus Finish(BrowserAttemptState state, string message)
    {
        Verifier = null;
        return Status = Status with { State = state, Message = message };
    }
}
