namespace VibeRe.Server.Application.Providers.Auth;

public enum BrowserAttemptState { Waiting, Completed, Failed, Expired, Canceled }
public sealed record BrowserAttemptStatus(
    string AttemptId, string SignInUrl, DateTimeOffset ExpiresAt,
    BrowserAttemptState State, string Message, ProviderAuthStatus? AuthStatus = null);

public interface IBrowserAuthService
{
    Task<BrowserAttemptStatus> StartAsync(string provider, CancellationToken cancellationToken = default);
    Task<BrowserAttemptStatus> PollAsync(string attemptId, CancellationToken cancellationToken = default);
    Task<BrowserAttemptStatus> CancelAsync(string attemptId, CancellationToken cancellationToken = default);
    Task CancelProviderAsync(string provider, CancellationToken cancellationToken = default);
}
