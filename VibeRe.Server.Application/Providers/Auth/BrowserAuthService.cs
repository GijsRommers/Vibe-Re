using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace VibeRe.Server.Application.Providers.Auth;

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
        if (!_flows.TryGetValue(provider, out var flow))
        {
            throw new ArgumentException("Unsupported provider.", nameof(provider));
        }

        await _startGate.WaitAsync(cancellationToken);
        try
        {
            MakeRoomForAttempt();
            var verifier = Encode(RandomNumberGenerator.GetBytes(64));
            var challenge = Encode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
            var process = await flow.StartAsync(challenge, cancellationToken);
            var attempt = CreateAttempt(provider, verifier, process);
            _attempts[attempt.Status.AttemptId] = attempt;
            return attempt.Status;
        }
        finally
        {
            _startGate.Release();
        }
    }

    private void MakeRoomForAttempt()
    {
        var now = time.GetUtcNow();
        foreach (var entry in _attempts.Where(x => x.Value.Status.ExpiresAt <= now))
        {
            // An in-flight exchange can still save a credential. Keep it discoverable until it
            // releases its gate, so provider logout can wait for it before removing credentials.
            if (!entry.Value.Gate.Wait(0)) continue;
            try
            {
                if (entry.Value.Status.State == BrowserAttemptState.Waiting)
                    entry.Value.Finish(BrowserAttemptState.Expired, "Browser login expired. Start again.");
                _attempts.TryRemove(entry.Key, out _);
            }
            finally
            {
                entry.Value.Gate.Release();
            }
        }

        if (_attempts.Count(x => x.Value.Status.State == BrowserAttemptState.Waiting) >= MaxPendingAttempts)
        {
            throw new BrowserLoginCapacityException();
        }

        // Keep recent terminal results for repeated polls without allowing unbounded retention.
        var excess = Math.Max(0, _attempts.Count - MaxRetainedAttempts + 1);
        var oldestTerminalAttempts = _attempts
            .Where(x => x.Value.Status.State != BrowserAttemptState.Waiting)
            .OrderBy(x => x.Value.Status.ExpiresAt)
            .Take(excess);
        foreach (var entry in oldestTerminalAttempts)
        {
            _attempts.TryRemove(entry.Key, out _);
        }
    }

    private BrowserLoginAttempt CreateAttempt(string provider, string verifier, BrowserProcess process)
    {
        var maximumExpiry = time.GetUtcNow().AddMinutes(10);
        var expires = process.ExpiresAt < maximumExpiry ? process.ExpiresAt : maximumExpiry;
        var id = Encode(RandomNumberGenerator.GetBytes(32));
        var status = new BrowserAttemptStatus(id, process.SignInUri.AbsoluteUri, expires,
            BrowserAttemptState.Waiting, "Open the browser to log this server in.");
        return new(provider, verifier, process, status);
    }

    public async Task<BrowserAttemptStatus> PollAsync(string attemptId, CancellationToken cancellationToken = default)
    {
        var attempt = Find(attemptId);
        await attempt.Gate.WaitAsync(cancellationToken);
        try
        {
            if (attempt.Status.State != BrowserAttemptState.Waiting)
            {
                return attempt.Status;
            }

            if (time.GetUtcNow() >= attempt.Status.ExpiresAt)
            {
                return attempt.Finish(BrowserAttemptState.Expired, "Browser login expired. Start again.");
            }

            if (time.GetUtcNow() < attempt.NextPollAt)
            {
                return attempt.Status;
            }

            attempt.NextPollAt = time.GetUtcNow().AddSeconds(3);
            try
            {
                var flow = _flows[attempt.Provider];
                var poll = await flow.PollAsync(attempt.Process.PollUri, cancellationToken);
                switch (poll.State)
                {
                    case BrowserPollState.Pending:
                        return attempt.Status;
                    case BrowserPollState.Completed when !string.IsNullOrWhiteSpace(poll.ExchangeToken):
                        return await CompleteAsync(attempt, flow, poll.ExchangeToken, cancellationToken);
                    case BrowserPollState.Expired:
                        return attempt.Finish(BrowserAttemptState.Expired, "Browser login expired. Start again.");
                    case BrowserPollState.Denied:
                        return attempt.Finish(BrowserAttemptState.Failed, "Browser login was denied.");
                    default:
                        return attempt.Finish(BrowserAttemptState.Failed, "Browser login failed. Start again.");
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                return attempt.Finish(BrowserAttemptState.Failed, "Could not complete browser login. Start again.");
            }
        }
        finally
        {
            attempt.Gate.Release();
        }
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
        finally
        {
            attempt.Gate.Release();
        }
    }

    public async Task CancelProviderAsync(string provider, CancellationToken cancellationToken = default)
    {
        await _startGate.WaitAsync(cancellationToken);
        try
        {
            foreach (var entry in _attempts.Where(x => x.Value.Provider == provider))
            {
                await CancelAsync(entry.Key, cancellationToken);
            }
        }
        finally
        {
            _startGate.Release();
        }
    }

    private BrowserLoginAttempt Find(string id) => _attempts.TryGetValue(id, out var attempt)
        ? attempt : throw new ArgumentException("Browser login not found or expired. Start again.");

    private static string Encode(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
