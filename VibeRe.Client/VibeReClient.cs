using Grpc.Core;
using Grpc.Net.Client;
using VibeRe.Protocol;

namespace VibeRe.Client;

public sealed class VibeReClient : IVibeReClient
{
    private readonly GrpcChannel _channel;
    private readonly ProviderAuth.ProviderAuthClient _auth;
    private readonly Metadata _headers;

    public VibeReClient(string address, string? accessToken = null, HttpMessageHandler? httpHandler = null)
    {
        _channel = GrpcChannel.ForAddress(ServerAddress.Normalize(address), new GrpcChannelOptions
        {
            HttpHandler = httpHandler ?? new SocketsHttpHandler { AllowAutoRedirect = false }
        });
        _auth = new(_channel);
        _headers = new();
        if (!string.IsNullOrWhiteSpace(accessToken)) _headers.Add("authorization", "Bearer " + accessToken);
    }

    public Task<AuthStatusReply> LoginAsync(string apiKey, CancellationToken cancellationToken = default) =>
        _auth.LoginAsync(new() { Provider = "mistral", ApiKey = apiKey }, Options(cancellationToken)).ResponseAsync;

    public Task<AuthStatusReply> GetStatusAsync(CancellationToken cancellationToken = default) =>
        _auth.GetStatusAsync(new() { Provider = "mistral" }, Options(cancellationToken)).ResponseAsync;

    public Task<AuthStatusReply> LogoutAsync(CancellationToken cancellationToken = default) =>
        _auth.LogoutAsync(new() { Provider = "mistral" }, Options(cancellationToken)).ResponseAsync;

    public Task<BrowserLoginReply> StartBrowserLoginAsync(CancellationToken cancellationToken = default) =>
        _auth.StartBrowserLoginAsync(new() { Provider = "mistral" }, Options(cancellationToken)).ResponseAsync;

    public Task<BrowserLoginReply> PollBrowserLoginAsync(string attemptId, CancellationToken cancellationToken = default) =>
        _auth.PollBrowserLoginAsync(new() { AttemptId = attemptId }, Options(cancellationToken)).ResponseAsync;

    public Task<BrowserLoginReply> CancelBrowserLoginAsync(string attemptId, CancellationToken cancellationToken = default) =>
        _auth.CancelBrowserLoginAsync(new() { AttemptId = attemptId }, Options(cancellationToken)).ResponseAsync;

    public async Task<BrowserLoginReply> LoginWithBrowserAsync(Func<BrowserLoginReply, Task> showBrowser,
        CancellationToken cancellationToken = default)
    {
        var attempt = await StartBrowserLoginAsync(cancellationToken);
        try
        {
            if (!Uri.TryCreate(attempt.SignInUrl, UriKind.Absolute, out var signInUri) ||
                signInUri.Scheme != "https" || signInUri.Host != "console.mistral.ai" ||
                signInUri.Port != 443 || signInUri.UserInfo.Length != 0)
                throw new InvalidOperationException("The server returned an untrusted browser login URL.");
            await showBrowser(attempt);
            var expiry = DateTimeOffset.FromUnixTimeSeconds(attempt.ExpiresAtUnixSeconds);
            while (DateTimeOffset.UtcNow < expiry)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var result = await PollBrowserLoginAsync(attempt.AttemptId, cancellationToken);
                if (result.State != BrowserLoginState.Waiting) return result;
                await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
            }
        }
        catch
        {
            await CleanupBrowserLoginAsync(attempt.AttemptId);
            throw;
        }
        await CleanupBrowserLoginAsync(attempt.AttemptId);
        cancellationToken.ThrowIfCancellationRequested();
        return new() { State = BrowserLoginState.Expired, Message = "Browser login expired. Start again." };
    }

    private async Task CleanupBrowserLoginAsync(string attemptId)
    {
        // Cleanup must not make cancellation or expiry wait for the normal 45-second RPC deadline.
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try { await CancelBrowserLoginAsync(attemptId, cleanup.Token); }
        catch { /* Preserve the original result or error, including caller cancellation. */ }
    }

    public static bool NeedsLogin(AuthStatusReply status) => status.State is AuthState.SignedOut or AuthState.InvalidCredential;
    private CallOptions Options(CancellationToken cancellationToken) =>
        new(_headers, deadline: DateTime.UtcNow.AddSeconds(45), cancellationToken: cancellationToken);
    public void Dispose() => _channel.Dispose();
}
