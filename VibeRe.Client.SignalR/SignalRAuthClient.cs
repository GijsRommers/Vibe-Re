using System.Net;
using System.Net.WebSockets;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using VibeRe.Client.Application;
using Contracts = VibeRe.Contracts;

namespace VibeRe.Client.SignalR;

// Owns the supplied connection; the platform chooses its transport and authentication.
public sealed partial class SignalRAuthClient(HubConnection connection) : IVibeReClient, VibeRe.Client.Application.Workspace.IWorkspaceClient
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan MaximumBrowserLoginLifetime = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan BrowserCleanupTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ConnectionPollInterval = TimeSpan.FromMilliseconds(100);

    private readonly HubConnection _connection = connection ?? throw new ArgumentNullException(nameof(connection));
    private readonly SemaphoreSlim _connectGate = new(1, 1);

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
        CancellationTokenSource? recovery = null;
        try
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
                        recovery?.Dispose();
                        recovery = null;
                        if (result.State != BrowserLoginState.Waiting)
                        {
                            return result;
                        }
                    }

                    throw new ServerConnectionException("The server ended the login stream before completion. Try again.");
                }
                catch (Exception error) when (IsConnectionLoss(error))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    // Only observation is replayed. Keep one deadline across consecutive failures,
                    // and yield even if automatic reconnect already reports Connected.
                    if (recovery is null)
                    {
                        recovery = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                        recovery.CancelAfter(RequestTimeout);
                    }
                    await Task.Delay(ConnectionPollInterval, recovery.Token);
                    await EnsureConnectedAsync(recovery.Token);
                }
            }
        }
        finally { recovery?.Dispose(); }
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

    private static bool IsConnectionLoss(Exception error) =>
        error is IOException or WebSocketException or TimeoutException or OperationCanceledException ||
        // SignalR checks its active transport under a lock, independently of public State.
        // A reconnect may finish before this catch runs, so re-reading State loses the failure.
        // Match only this transport error; unrelated InvalidOperation/Hub/auth errors stay terminal.
        error is InvalidOperationException
        {
            Message: "The 'StreamAsChannelCoreAsync' method cannot be called if the connection is not active"
        };

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
        }
    }
}
