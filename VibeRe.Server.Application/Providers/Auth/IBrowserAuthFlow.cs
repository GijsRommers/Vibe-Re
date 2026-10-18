namespace VibeRe.Server.Application.Providers.Auth;

public sealed record BrowserProcess(string ProcessId, Uri SignInUri, Uri PollUri, DateTimeOffset ExpiresAt);

public enum BrowserPollState
{
    Pending,
    Completed,
    Expired,
    Denied,
    Failed
}

public sealed record BrowserPoll(BrowserPollState State, string? ExchangeToken = null);

public interface IBrowserAuthFlow
{
    string ProviderName { get; }
    Task<BrowserProcess> StartAsync(string challenge, CancellationToken cancellationToken);
    Task<BrowserPoll> PollAsync(Uri pollUri, CancellationToken cancellationToken);
    Task<string> ExchangeAsync(string processId, string exchangeToken, string verifier, CancellationToken cancellationToken);
}
