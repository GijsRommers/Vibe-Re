using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace VibeRe.Application.Providers.Auth;

// The client gets only a browser URL and opaque attempt ID; PKCE and exchange stay on the server.
public sealed class BrowserAuthService(IEnumerable<IBrowserAuthFlow> flows, IAuthService auth, TimeProvider time)
    : IBrowserAuthService
{
    private const int MaxPendingAttempts = 32;
    private const int MaxRetainedAttempts = 64;
    private readonly Dictionary<string, IBrowserAuthFlow> _flows = flows.ToDictionary(x => x.ProviderName);
    private readonly ConcurrentDictionary<string, BrowserLoginAttempt> _attempts = new();
    private readonly SemaphoreSlim _startGate = new(1, 1);

    public async Task<BrowserAttemptStatus> StartAsync(string provider, CancellationToken cancellationToken = default)
    {
        if (!_flows.TryGetValue(provider, out var flow)) throw new ArgumentException("Unsupported provider.");
        await _startGate.WaitAsync(cancellationToken);
        try
        {
            foreach (var entry in _attempts.Where(x => x.Value.Status.ExpiresAt <= time.GetUtcNow()))
                _attempts.TryRemove(entry.Key, out _);
            if (_attempts.Count(x => x.Value.Status.State == BrowserAttemptState.Waiting) >= MaxPendingAttempts)
                throw new InvalidOperationException("Too many pending browser logins.");
            // Keep recent terminal results for repeated polls without allowing unbounded retention.
            foreach (var entry in _attempts.Where(x => x.Value.Status.State != BrowserAttemptState.Waiting)
                         .OrderBy(x => x.Value.Status.ExpiresAt).Take(Math.Max(0, _attempts.Count - MaxRetainedAttempts + 1)))
                _attempts.TryRemove(entry.Key, out _);
            var verifier = Encode(RandomNumberGenerator.GetBytes(64));
            var challenge = Encode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
            var process = await flow.StartAsync(challenge, cancellationToken);
            var expires = process.ExpiresAt < time.GetUtcNow().AddMinutes(10)
                ? process.ExpiresAt : time.GetUtcNow().AddMinutes(10);
            var id = Encode(RandomNumberGenerator.GetBytes(32));
            var status = new BrowserAttemptStatus(id, process.SignInUri.AbsoluteUri, expires,
                BrowserAttemptState.Waiting, "Open the browser to log this server in to Mistral.");
            _attempts[id] = new(provider, verifier, process, status);
            return status;
        }
        finally { _startGate.Release(); }
    }

    public async Task<BrowserAttemptStatus> PollAsync(string attemptId, CancellationToken cancellationToken = default)
    {
        var attempt = Find(attemptId);
        await attempt.Gate.WaitAsync(cancellationToken);
        try
        {
            if (attempt.Status.State != BrowserAttemptState.Waiting) return attempt.Status;
            if (time.GetUtcNow() >= attempt.Status.ExpiresAt)
                return attempt.Finish(BrowserAttemptState.Expired, "Browser login expired. Start again.");
            if (time.GetUtcNow() < attempt.NextPollAt) return attempt.Status;
            attempt.NextPollAt = time.GetUtcNow().AddSeconds(3);
            try
            {
                var flow = _flows[attempt.Provider];
                var poll = await flow.PollAsync(attempt.Process.PollUri, cancellationToken);
                switch (poll.State)
                {
                    case "pending": return attempt.Status;
                    case "completed" when !string.IsNullOrWhiteSpace(poll.ExchangeToken):
                        return await CompleteAsync(attempt, flow, poll.ExchangeToken, cancellationToken);
                    case "expired": return attempt.Finish(BrowserAttemptState.Expired, "Browser login expired. Start again.");
                    case "denied": return attempt.Finish(BrowserAttemptState.Failed, "Browser login was denied.");
                    default: return attempt.Finish(BrowserAttemptState.Failed, "Mistral browser login failed. Start again.");
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch
            {
                return attempt.Finish(BrowserAttemptState.Failed, "Could not complete Mistral browser login. Start again.");
            }
        }
        finally { attempt.Gate.Release(); }
    }

    private async Task<BrowserAttemptStatus> CompleteAsync(BrowserLoginAttempt attempt, IBrowserAuthFlow flow,
        string exchangeToken, CancellationToken cancellationToken)
    {
        var key = await flow.ExchangeAsync(attempt.Process.ProcessId, exchangeToken,
            attempt.Verifier!, cancellationToken);
        // Both login methods use exactly the same validation and persistence action.
        var result = await auth.LoginAsync(attempt.Provider, key, cancellationToken);
        attempt.Status = attempt.Status with { AuthStatus = result };
        return attempt.Finish(
            result.State is ProviderAuthState.Authenticated or ProviderAuthState.SubscriptionRequired
                ? BrowserAttemptState.Completed : BrowserAttemptState.Failed, result.Message);
    }

    public async Task<BrowserAttemptStatus> CancelAsync(string attemptId, CancellationToken cancellationToken = default)
    {
        var attempt = Find(attemptId);
        await attempt.Gate.WaitAsync(cancellationToken);
        try
        {
            return attempt.Status.State == BrowserAttemptState.Waiting
                ? attempt.Finish(BrowserAttemptState.Canceled, "Browser login canceled.") : attempt.Status;
        }
        finally { attempt.Gate.Release(); }
    }

    public async Task CancelProviderAsync(string provider, CancellationToken cancellationToken = default)
    {
        await _startGate.WaitAsync(cancellationToken);
        try
        {
            foreach (var entry in _attempts.Where(x => x.Value.Provider == provider))
                await CancelAsync(entry.Key, cancellationToken);
        }
        finally { _startGate.Release(); }
    }

    private BrowserLoginAttempt Find(string id) => _attempts.TryGetValue(id, out var attempt)
        ? attempt : throw new ArgumentException("Browser login not found or expired. Start again.");

    private static string Encode(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
