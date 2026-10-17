using Grpc.Core;
using Grpc.Net.Client;
using VibeRe.Client.Configuration;
using VibeRe.Protocol;

namespace VibeRe.Client;

public sealed class VibeReClient : IVibeReClient
{
    private readonly GrpcChannel _channel;
    private readonly ProviderAuth.ProviderAuthClient _auth;
    private readonly Metadata _headers;
    public ServerConnection Server { get; }

    public VibeReClient(ServerConnection server, string? accessToken = null, HttpMessageHandler? httpHandler = null)
    {
        Server = server;
        var address = ServerConnection.NormalizeAddress(server.Address);
        _channel = GrpcChannel.ForAddress(address, new GrpcChannelOptions
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
            await CancelBrowserLoginAsync(attempt.AttemptId, cancellationToken);
            return new() { State = BrowserLoginState.Expired, Message = "Browser login expired. Start again." };
        }
        catch
        {
            try { await CancelBrowserLoginAsync(attempt.AttemptId); } catch { /* Preserve the original error. */ }
            throw;
        }
    }

    public static bool NeedsLogin(AuthStatusReply status) => status.State is AuthState.SignedOut or AuthState.InvalidCredential;
    private CallOptions Options(CancellationToken cancellationToken) =>
        new(_headers, deadline: DateTime.UtcNow.AddSeconds(45), cancellationToken: cancellationToken);
    public void Dispose() => _channel.Dispose();
}
