using Grpc.Core;
using Grpc.Net.Client;
using Protocol = VibeRe.Protocol;

namespace VibeRe.Client.Infrastructure;

public sealed class VibeReClient : IVibeReClient, IDisposable
{
    private readonly GrpcChannel _channel;
    private readonly Protocol.ProviderAuth.ProviderAuthClient _auth;
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

    public async Task<AuthStatus> LoginAsync(string apiKey, CancellationToken cancellationToken = default) =>
        Map(await SendAsync(() => _auth.LoginAsync(new() { Provider = "mistral", ApiKey = apiKey },
            Options(cancellationToken)).ResponseAsync, cancellationToken));

    public async Task<AuthStatus> GetStatusAsync(CancellationToken cancellationToken = default) =>
        Map(await SendAsync(() => _auth.GetStatusAsync(new() { Provider = "mistral" },
            Options(cancellationToken)).ResponseAsync, cancellationToken));

    public async Task<AuthStatus> LogoutAsync(CancellationToken cancellationToken = default) =>
        Map(await SendAsync(() => _auth.LogoutAsync(new() { Provider = "mistral" },
            Options(cancellationToken)).ResponseAsync, cancellationToken));

    private Task<Protocol.BrowserLoginReply> StartBrowserLoginAsync(CancellationToken cancellationToken) =>
        SendAsync(() => _auth.StartBrowserLoginAsync(new() { Provider = "mistral" },
            Options(cancellationToken)).ResponseAsync, cancellationToken);

    private Task<Protocol.BrowserLoginReply> PollBrowserLoginAsync(string attemptId, CancellationToken cancellationToken) =>
        SendAsync(() => _auth.PollBrowserLoginAsync(new() { AttemptId = attemptId },
            Options(cancellationToken)).ResponseAsync, cancellationToken);

    private Task<Protocol.BrowserLoginReply> CancelBrowserLoginAsync(string attemptId, CancellationToken cancellationToken) =>
        SendAsync(() => _auth.CancelBrowserLoginAsync(new() { AttemptId = attemptId },
            Options(cancellationToken)).ResponseAsync, cancellationToken);

    public async Task<BrowserLoginResult> LoginWithBrowserAsync(Func<BrowserLoginResult, Task> showBrowser,
        CancellationToken cancellationToken = default)
    {
        var attempt = await StartBrowserLoginAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Uri.TryCreate(attempt.SignInUrl, UriKind.Absolute, out var signInUri) ||
                signInUri.Scheme != "https" || signInUri.Host != "console.mistral.ai" ||
                signInUri.Port != 443 || signInUri.UserInfo.Length != 0)
                throw new ServerConnectionException("The server returned an untrusted browser login URL.");
            await showBrowser(Map(attempt));
            var expiry = DateTimeOffset.FromUnixTimeSeconds(attempt.ExpiresAtUnixSeconds);
            while (DateTimeOffset.UtcNow < expiry)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var result = Map(await PollBrowserLoginAsync(attempt.AttemptId, cancellationToken));
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

    private static async Task<T> SendAsync<T>(Func<Task<T>> operation, CancellationToken cancellationToken)
    {
        try { return await operation(); }
        catch (RpcException error) when (error.StatusCode == StatusCode.Cancelled && cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException("The request was canceled.", error, cancellationToken);
        }
        catch (RpcException error)
        {
            var message = error.StatusCode == StatusCode.Unavailable
                ? "Could not connect to this server. Check its address and whether it is running."
                : string.IsNullOrWhiteSpace(error.Status.Detail) ? "The server request failed." : error.Status.Detail;
            throw new ServerConnectionException(message, error);
        }
    }

    private static AuthStatus Map(Protocol.AuthStatusReply reply) => new()
    {
        State = reply.State switch
        {
            Protocol.AuthState.SignedOut => AuthState.SignedOut,
            Protocol.AuthState.Authenticated => AuthState.Authenticated,
            Protocol.AuthState.InvalidCredential => AuthState.InvalidCredential,
            Protocol.AuthState.SubscriptionRequired => AuthState.SubscriptionRequired,
            Protocol.AuthState.Unavailable => AuthState.Unavailable,
            _ => throw new ServerConnectionException("The server returned an unsupported authentication status.")
        },
        Message = reply.Message
    };

    private static BrowserLoginResult Map(Protocol.BrowserLoginReply reply) => new()
    {
        SignInUrl = reply.SignInUrl,
        State = reply.State switch
        {
            Protocol.BrowserLoginState.Waiting => BrowserLoginState.Waiting,
            Protocol.BrowserLoginState.Completed => BrowserLoginState.Completed,
            Protocol.BrowserLoginState.Failed => BrowserLoginState.Failed,
            Protocol.BrowserLoginState.Expired => BrowserLoginState.Expired,
            Protocol.BrowserLoginState.Canceled => BrowserLoginState.Canceled,
            _ => throw new ServerConnectionException("The server returned an unsupported browser login status.")
        },
        Message = reply.Message,
        AuthStatus = reply.AuthStatus is { } status ? Map(status) : null
    };

    private CallOptions Options(CancellationToken cancellationToken) =>
        new(_headers, deadline: DateTime.UtcNow.AddSeconds(45), cancellationToken: cancellationToken);
    public ValueTask DisposeAsync()
    {
        _channel.Dispose();
        return ValueTask.CompletedTask;
    }

    public void Dispose() => _channel.Dispose();
}
