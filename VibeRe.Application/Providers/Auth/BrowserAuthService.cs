using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace VibeRe.Application.Providers.Auth;

// The client gets only a browser URL and opaque attempt ID; PKCE and exchange stay on the server.
public sealed class BrowserAuthService(IEnumerable<IBrowserAuthFlow> flows, IAuthService auth, TimeProvider time)
    : IBrowserAuthService
{
    private readonly Dictionary<string, IBrowserAuthFlow> _flows = flows.ToDictionary(x => x.ProviderName);
    private readonly ConcurrentDictionary<string, Attempt> _attempts = new();
    private readonly SemaphoreSlim _startGate = new(1, 1);

    public async Task<BrowserAttemptStatus> StartAsync(string provider, CancellationToken cancellationToken = default)
    {
        if (!_flows.TryGetValue(provider, out var flow)) throw new ArgumentException("Unsupported provider.");
        await _startGate.WaitAsync(cancellationToken);
        try
        {
            foreach (var entry in _attempts.Where(x => x.Value.Status.ExpiresAt <= time.GetUtcNow()))
                _attempts.TryRemove(entry.Key, out _);
            if (_attempts.Count >= 32) throw new InvalidOperationException("Too many pending browser logins.");
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
                return Finish(attempt, BrowserAttemptState.Expired, "Browser login expired. Start again.");
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
                        var key = await flow.ExchangeAsync(attempt.Process.ProcessId, poll.ExchangeToken,
                            attempt.Verifier!, cancellationToken);
                        // Both login methods use exactly the same validation and persistence action.
                        var result = await auth.LoginAsync(attempt.Provider, key, cancellationToken);
                        attempt.Status = attempt.Status with { AuthStatus = result };
                        return Finish(attempt,
                            result.State is ProviderAuthState.Authenticated or ProviderAuthState.SubscriptionRequired
                                ? BrowserAttemptState.Completed : BrowserAttemptState.Failed, result.Message);
                    case "expired": return Finish(attempt, BrowserAttemptState.Expired, "Browser login expired. Start again.");
                    case "denied": return Finish(attempt, BrowserAttemptState.Failed, "Browser login was denied.");
                    default: return Finish(attempt, BrowserAttemptState.Failed, "Mistral browser login failed. Start again.");
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch
            {
                return Finish(attempt, BrowserAttemptState.Failed, "Could not complete Mistral browser login. Start again.");
            }
        }
        finally { attempt.Gate.Release(); }
    }

    public async Task<BrowserAttemptStatus> CancelAsync(string attemptId, CancellationToken cancellationToken = default)
    {
        var attempt = Find(attemptId);
        await attempt.Gate.WaitAsync(cancellationToken);
        try
        {
            return attempt.Status.State == BrowserAttemptState.Waiting
                ? Finish(attempt, BrowserAttemptState.Canceled, "Browser login canceled.") : attempt.Status;
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

    private Attempt Find(string id) => _attempts.TryGetValue(id, out var attempt)
        ? attempt : throw new ArgumentException("Browser login not found or expired. Start again.");

    private static BrowserAttemptStatus Finish(Attempt attempt, BrowserAttemptState state, string message)
    {
        attempt.Verifier = null;
        return attempt.Status = attempt.Status with { State = state, Message = message };
    }

    private static string Encode(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private sealed class Attempt(string provider, string verifier, BrowserProcess process, BrowserAttemptStatus status)
    {
        public string Provider { get; } = provider;
        public string? Verifier { get; set; } = verifier;
        public BrowserProcess Process { get; } = process;
        public BrowserAttemptStatus Status { get; set; } = status;
        public DateTimeOffset NextPollAt { get; set; }
        public SemaphoreSlim Gate { get; } = new(1, 1);
    }
}
