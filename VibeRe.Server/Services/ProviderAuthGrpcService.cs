using Grpc.Core;
using VibeRe.Server.Application.Providers.Auth;
using VibeRe.Protocol;

namespace VibeRe.Server.Services;

public sealed class ProviderAuthGrpcService(IAuthService auth, IBrowserAuthService browser, ILogger<ProviderAuthGrpcService> logger)
    : ProviderAuth.ProviderAuthBase
{
    public override Task<AuthStatusReply> Login(LoginRequest request, ServerCallContext context) =>
        ExecuteAsync(() => auth.LoginAsync(request.Provider, request.ApiKey, context.CancellationToken), "Enter a valid API key.");

    public override Task<AuthStatusReply> GetStatus(ProviderRequest request, ServerCallContext context) =>
        ExecuteAsync(() => auth.GetStatusAsync(request.Provider, context.CancellationToken));

    public override Task<AuthStatusReply> Logout(ProviderRequest request, ServerCallContext context) =>
        ExecuteAsync(async () =>
        {
            await browser.CancelProviderAsync(request.Provider, context.CancellationToken);
            await auth.LogoutAsync(request.Provider, context.CancellationToken);
            return new(ProviderAuthState.SignedOut, "Mistral credential removed from this server.");
        });

    public override Task<BrowserLoginReply> StartBrowserLogin(ProviderRequest request, ServerCallContext context) =>
        ExecuteBrowserAsync(() => browser.StartAsync(request.Provider, context.CancellationToken));

    public override Task<BrowserLoginReply> PollBrowserLogin(BrowserAttemptRequest request, ServerCallContext context) =>
        ExecuteBrowserAsync(() => browser.PollAsync(request.AttemptId, context.CancellationToken));

    public override Task<BrowserLoginReply> CancelBrowserLogin(BrowserAttemptRequest request, ServerCallContext context) =>
        ExecuteBrowserAsync(() => browser.CancelAsync(request.AttemptId, context.CancellationToken));

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
            throw new RpcException(new Status(StatusCode.ResourceExhausted,
                "Too many pending browser logins. Cancel an existing login or wait for it to expire."));
        }
        catch (ArgumentException)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument,
                "Browser login not found or expired. Start again."));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            logger.LogError("Browser authentication failed ({ExceptionType}).", e.GetType().Name);
            throw new RpcException(new Status(StatusCode.Unavailable, "Could not start Mistral browser login. Try again."));
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
            throw new RpcException(new Status(StatusCode.InvalidArgument, invalidRequestMessage));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            // Do not log exception messages: secret-store errors can contain credentials.
            logger.LogError("Provider authentication failed ({ExceptionType}).", e.GetType().Name);
            throw new RpcException(new Status(StatusCode.Internal, "Could not access server credential storage."));
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
