using System.Net;
using System.Net.WebSockets;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Contracts = VibeRe.Contracts;

namespace VibeRe.Client.Infrastructure;

public sealed class VibeReClient : IVibeReClient, IDisposable
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan MaximumBrowserLoginLifetime = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan BrowserCleanupTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ConnectionPollInterval = TimeSpan.FromMilliseconds(100);

    private readonly HubConnection _connection;
    private readonly SemaphoreSlim _connectGate = new(1, 1);
    private readonly HttpMessageInvoker _http;

    public VibeReClient(string address, string? accessToken = null, HttpMessageHandler? httpHandler = null)
    {
        var endpoint = new Uri(new Uri(ServerAddress.Normalize(address)), Contracts.ServerProtocol.AuthHubPath);
        _http = new HttpMessageInvoker(httpHandler ?? new SocketsHttpHandler { AllowAutoRedirect = false });
        _connection = new HubConnectionBuilder().WithUrl(endpoint, options =>
        {
            options.AccessTokenProvider = () => Task.FromResult(accessToken);
            // Avoid both SignalR negotiation redirects and HTTP redirects during the WebSocket upgrade.
            options.Transports = httpHandler is null ? HttpTransportType.WebSockets : HttpTransportType.LongPolling;
            options.SkipNegotiation = httpHandler is null;
            if (httpHandler is not null)
            {
                options.HttpMessageHandlerFactory = _ => new SharedHandler(_http, endpoint);
            }
            else
            {
                options.WebSocketFactory = async (context, cancellationToken) =>
                {
                    var socket = new ClientWebSocket();
                    socket.Options.CollectHttpResponseDetails = true;
                    if (!string.IsNullOrWhiteSpace(accessToken))
                    {
                        socket.Options.SetRequestHeader("Authorization", "Bearer " + accessToken);
                    }

                    try
                    {
                        await socket.ConnectAsync(context.Uri, _http, cancellationToken);
                        return socket;
                    }
                    catch (Exception error)
                    {
                        var status = socket.HttpStatusCode;
                        socket.Dispose();
                        if (status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                        {
                            throw new HttpRequestException("Server access denied.", error, status);
                        }

                        throw;
                    }
                };
            }
        }).WithAutomaticReconnect([TimeSpan.Zero, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5)]).Build();
    }

    public async Task<AuthStatus> LoginAsync(string apiKey, CancellationToken cancellationToken = default) =>
        Map(await InvokeAsync<Contracts.AuthStatusReply>("Login", [apiKey], cancellationToken));

    public async Task<AuthStatus> GetStatusAsync(CancellationToken cancellationToken = default) =>
        Map(await InvokeAsync<Contracts.AuthStatusReply>("GetStatus", [], cancellationToken));

    public async Task<AuthStatus> LogoutAsync(CancellationToken cancellationToken = default) =>
        Map(await InvokeAsync<Contracts.AuthStatusReply>("Logout", [], cancellationToken));

    public async Task<BrowserLoginResult> LoginWithBrowserAsync(Func<BrowserLoginResult, Task> showBrowser,
        CancellationToken cancellationToken = default)
    {
        var attempt = await StartBrowserLoginAsync(showBrowser, cancellationToken);
        using var expiry = CreateBrowserLoginLifetime(attempt.ExpiresAt, cancellationToken);
        try
        {
            return await WatchBrowserLoginAsync(attempt.AttemptId, expiry.Token);
        }
        catch (OperationCanceledException) when (expiry.IsCancellationRequested)
        {
            await CleanupBrowserLoginAsync(attempt.AttemptId);
            cancellationToken.ThrowIfCancellationRequested();
            return new()
            {
                State = BrowserLoginState.Expired,
                Message = "Browser login expired. Start again."
            };
        }
        catch (Exception error) when (error is ServerConnectionException or HubException)
        {
            await CleanupBrowserLoginAsync(attempt.AttemptId);
            throw Translate(error, cancellationToken);
        }
        catch (Exception error)
        {
            // A lost UI connection must not invalidate the server's pending login.
            throw Translate(error, cancellationToken);
        }
    }

    private async Task<(string AttemptId, DateTimeOffset ExpiresAt)> StartBrowserLoginAsync(
        Func<BrowserLoginResult, Task> showBrowser, CancellationToken cancellationToken)
    {
        var attempt = await InvokeAsync<Contracts.BrowserLoginReply>("StartBrowserLogin", [], cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Provider host allowlists belong to the server's provider adapter.
            if (!Uri.TryCreate(attempt.SignInUrl, UriKind.Absolute, out var uri) ||
                !uri.IsWellFormedOriginalString() || uri.Scheme != Uri.UriSchemeHttps ||
                uri.Host.Length == 0 || uri.UserInfo.Length != 0)
            {
                throw new ServerConnectionException("The server returned an untrusted browser login URL.");
            }

            var expiresAt = DateTimeOffset.FromUnixTimeSeconds(attempt.ExpiresAtUnixSeconds);
            await showBrowser(Map(attempt));
            return (attempt.AttemptId, expiresAt);
        }
        catch
        {
            await CleanupBrowserLoginAsync(attempt.AttemptId);
            throw;
        }
    }

    private static CancellationTokenSource CreateBrowserLoginLifetime(
        DateTimeOffset expiresAt, CancellationToken cancellationToken)
    {
        var expiry = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var remaining = expiresAt - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero)
        {
            expiry.Cancel();
        }
        else
        {
            expiry.CancelAfter(remaining > MaximumBrowserLoginLifetime ? MaximumBrowserLoginLifetime : remaining);
        }

        return expiry;
    }

    private async Task<BrowserLoginResult> WatchBrowserLoginAsync(string attemptId, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await foreach (var reply in _connection.StreamAsync<Contracts.BrowserLoginReply>(
                    "WatchBrowserLogin", attemptId, cancellationToken))
                {
                    var result = Map(reply);
                    if (result.State != BrowserLoginState.Waiting)
                    {
                        return result;
                    }
                }

                throw new ServerConnectionException("The server ended the login stream before completion. Try again.");
            }
            catch (Exception error) when (IsConnectionLoss(error) && !cancellationToken.IsCancellationRequested)
            {
                // Only observation is replayed. The server retains the same attempt across reconnects.
                using var recovery = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                recovery.CancelAfter(RequestTimeout);
                await EnsureConnectedAsync(recovery.Token);
            }
        }
    }

    private async Task<T> InvokeAsync<T>(string method, object?[] arguments, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);
        try
        {
            await EnsureConnectedAsync(timeout.Token);
            return await _connection.InvokeCoreAsync<T>(method, arguments, timeout.Token);
        }
        catch (Exception error)
        {
            throw Translate(error, cancellationToken);
        }
    }

    private async Task EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        await _connectGate.WaitAsync(cancellationToken);
        try
        {
            while (_connection.State is HubConnectionState.Connecting or HubConnectionState.Reconnecting)
            {
                await Task.Delay(ConnectionPollInterval, cancellationToken);
            }

            if (_connection.State == HubConnectionState.Disconnected)
            {
                await _connection.StartAsync(cancellationToken);
            }
        }
        finally
        {
            _connectGate.Release();
        }
    }

    private async Task CleanupBrowserLoginAsync(string attemptId)
    {
        using var cleanup = new CancellationTokenSource(BrowserCleanupTimeout);
        try
        {
            await InvokeAsync<Contracts.BrowserLoginReply>("CancelBrowserLogin", [attemptId], cleanup.Token);
        }
        catch
        {
            // Preserve the original result/error; server attempts also expire independently.
        }
    }

    private bool IsConnectionLoss(Exception error) =>
        error is IOException or WebSocketException or TimeoutException or OperationCanceledException ||
        error is InvalidOperationException && _connection.State != HubConnectionState.Connected;

    private static Exception Translate(Exception error, CancellationToken cancellationToken) => error switch
    {
        OperationCanceledException when cancellationToken.IsCancellationRequested => new OperationCanceledException("Canceled.", error, cancellationToken),
        OperationCanceledException => new ServerConnectionException("The server request timed out. Try again.", error),
        ServerConnectionException => error,
        HubException => new ServerConnectionException(error.Message, error),
        HttpRequestException { StatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden } =>
            new ServerConnectionException("Access denied. Check the access token for this server.", error),
        _ => new ServerConnectionException("Could not connect to this server. Check its address, access token, and whether it is running.", error)
    };

    private static AuthStatus Map(Contracts.AuthStatusReply reply) => new()
    {
        State = reply.State switch
        {
            Contracts.AuthState.SignedOut => AuthState.SignedOut,
            Contracts.AuthState.Authenticated => AuthState.Authenticated,
            Contracts.AuthState.InvalidCredential => AuthState.InvalidCredential,
            Contracts.AuthState.SubscriptionRequired => AuthState.SubscriptionRequired,
            Contracts.AuthState.Unavailable => AuthState.Unavailable,
            _ => throw new ServerConnectionException("The server returned an unsupported authentication status.")
        },
        Message = reply.Message
    };

    private static BrowserLoginResult Map(Contracts.BrowserLoginReply reply) => new()
    {
        SignInUrl = reply.SignInUrl,
        State = reply.State switch
        {
            Contracts.BrowserLoginState.Waiting => BrowserLoginState.Waiting,
            Contracts.BrowserLoginState.Completed => BrowserLoginState.Completed,
            Contracts.BrowserLoginState.Failed => BrowserLoginState.Failed,
            Contracts.BrowserLoginState.Expired => BrowserLoginState.Expired,
            Contracts.BrowserLoginState.Canceled => BrowserLoginState.Canceled,
            _ => throw new ServerConnectionException("The server returned an unsupported browser login status.")
        },
        Message = reply.Message,
        AuthStatus = reply.AuthStatus is { } status ? Map(status) : null
    };

    public async ValueTask DisposeAsync()
    {
        try
        {
            await _connection.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            _connectGate.Dispose();
            _http.Dispose();
        }
    }

    // Retain synchronous disposal for native callers.
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    // Reconnect creates a new HTTP pipeline without prematurely disposing an injected handler.
    private sealed class SharedHandler(HttpMessageInvoker http, Uri endpoint) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri?.GetLeftPart(UriPartial.Authority) != endpoint.GetLeftPart(UriPartial.Authority))
            {
                throw new HttpRequestException("A server connection cannot redirect to another destination.");
            }

            return http.SendAsync(request, cancellationToken);
        }
    }
}
