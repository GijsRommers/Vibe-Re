using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.SignalR;
using VibeRe.Contracts;
using VibeRe.Server.Application.Providers.Auth;

namespace VibeRe.Server.Services;

// The hub is only a transport adapter. Credentials and browser attempts belong to application services.
public sealed partial class ProviderAuthHub(
    IAuthService auth,
    VibeRe.Server.Application.Workspace.WorkspaceService workspace,
    IBrowserAuthService browser,
    IHostApplicationLifetime lifetime,
    DeviceAccessHubFilter access,
    ILogger<ProviderAuthHub> logger) : Hub
{
    private const string Provider = "mistral";
    private static readonly TimeSpan BrowserPollInterval = TimeSpan.FromSeconds(3);

    public Task<AuthStatusReply> Login(string apiKey) => ExecuteAsync(() =>
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new ArgumentException();
        }

        return auth.LoginAsync(Provider, apiKey, Context.ConnectionAborted);
    }, "Enter a valid API key.");

    public Task<AuthStatusReply> GetStatus() =>
        ExecuteAsync(() => auth.GetStatusAsync(Provider, Context.ConnectionAborted));

    public Task<AuthStatusReply> Logout() => ExecuteAsync(async () =>
    {
        await browser.CancelProviderAsync(Provider, Context.ConnectionAborted);
        await auth.LogoutAsync(Provider, Context.ConnectionAborted);
        return new(ProviderAuthState.SignedOut, "Mistral credential removed from this server.");
    });

    // A dropped connection must not cancel a server-owned attempt. The caller can watch its ID again.
    public Task<BrowserLoginReply> StartBrowserLogin() =>
        ExecuteBrowserAsync(() => browser.StartAsync(Provider, lifetime.ApplicationStopping));

    public Task<BrowserLoginReply> CancelBrowserLogin(string attemptId) =>
        ExecuteBrowserAsync(() => browser.CancelAsync(attemptId, lifetime.ApplicationStopping));

    public async IAsyncEnumerable<BrowserLoginReply> WatchBrowserLogin(string attemptId,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var watching = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken,
            Context.ConnectionAborted, lifetime.ApplicationStopping);
        while (!watching.IsCancellationRequested)
        {
            // Finish an in-flight exchange even if the watcher disconnects, so a consumed provider token
            // cannot strand the attempt. Only explicit cancel/logout changes an attempt to Canceled.
            await access.EnsureAuthorizedAsync(Context);
            var status = await ExecuteBrowserAsync(() => browser.PollAsync(attemptId, lifetime.ApplicationStopping));
            if (watching.IsCancellationRequested)
            {
                yield break;
            }

            await access.EnsureAuthorizedAsync(Context);
            if (watching.IsCancellationRequested)
            {
                yield break;
            }

            yield return status;
            if (status.State != BrowserLoginState.Waiting)
            {
                yield break;
            }

            try
            {
                await Task.Delay(BrowserPollInterval, watching.Token);
            }
            catch (OperationCanceledException) when (watching.IsCancellationRequested)
            {
                yield break;
            }
        }
    }

    private async Task<BrowserLoginReply> ExecuteBrowserAsync(Func<Task<BrowserAttemptStatus>> action)
    {
        try
        {
            var result = await action();
            return new()
            {
                AttemptId = result.AttemptId,
                SignInUrl = result.SignInUrl,
                ExpiresAtUnixSeconds = result.ExpiresAt.ToUnixTimeSeconds(),
                State = Map(result.State),
                Message = result.Message,
                AuthStatus = result.AuthStatus is { } status ? Map(status) : null
            };
        }
        catch (BrowserLoginCapacityException)
        {
            throw new HubException("Too many pending browser logins. Cancel an existing login or wait for it to expire.");
        }
        catch (ArgumentException)
        {
            throw new HubException("Browser login not found or expired. Start again.");
        }
        catch (OperationCanceledException)
        {
            throw new HubException("Browser login request canceled.");
        }
        catch (Exception e)
        {
            // Exception messages may contain credentials. Log only the exception type.
            logger.LogError("Browser authentication failed ({ExceptionType}).", e.GetType().Name);
            throw new HubException("Could not complete Mistral browser login. Try again.");
        }
    }

    private async Task<AuthStatusReply> ExecuteAsync(Func<Task<ProviderAuthStatus>> action,
        string invalidRequestMessage = "Invalid authentication request.")
    {
        try
        {
            return Map(await action());
        }
        catch (ArgumentException)
        {
            throw new HubException(invalidRequestMessage);
        }
        catch (OperationCanceledException)
        {
            throw new HubException("Authentication request canceled.");
        }
        catch (Exception e)
        {
            logger.LogError("Provider authentication failed ({ExceptionType}).", e.GetType().Name);
            throw new HubException("Could not access server credential storage.");
        }
    }

    private static AuthStatusReply Map(ProviderAuthStatus status) => new()
    {
        State = status.State switch
        {
            ProviderAuthState.SignedOut => AuthState.SignedOut,
            ProviderAuthState.Authenticated => AuthState.Authenticated,
            ProviderAuthState.InvalidCredential => AuthState.InvalidCredential,
            ProviderAuthState.SubscriptionRequired => AuthState.SubscriptionRequired,
            ProviderAuthState.Unavailable => AuthState.Unavailable,
            _ => throw new InvalidOperationException("Unknown provider authentication state.")
        },
        Message = status.Message
    };

    private static BrowserLoginState Map(BrowserAttemptState state) => state switch
    {
        BrowserAttemptState.Waiting => BrowserLoginState.Waiting,
        BrowserAttemptState.Completed => BrowserLoginState.Completed,
        BrowserAttemptState.Failed => BrowserLoginState.Failed,
        BrowserAttemptState.Expired => BrowserLoginState.Expired,
        BrowserAttemptState.Canceled => BrowserLoginState.Canceled,
        _ => throw new InvalidOperationException("Unknown browser login state.")
    };
}
