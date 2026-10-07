using Grpc.Core;
using VibeRe.Application.Providers.Auth;
using VibeRe.Protocol;

namespace VibeRe.Server.Services;

public sealed class ProviderAuthGrpcService(IAuthService auth, IBrowserAuthService browser, ILogger<ProviderAuthGrpcService> logger)
    : ProviderAuth.ProviderAuthBase
{
    public override Task<AuthStatusReply> Login(LoginRequest request, ServerCallContext context) =>
        ExecuteAsync(() => auth.LoginAsync(request.Provider, request.ApiKey, context.CancellationToken));

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

    private static async Task<BrowserLoginReply> ExecuteBrowserAsync(Func<Task<BrowserAttemptStatus>> action)
    {
        try
        {
            var result = await action();
            return new()
            {
                AttemptId = result.AttemptId, SignInUrl = result.SignInUrl,
                ExpiresAtUnixSeconds = result.ExpiresAt.ToUnixTimeSeconds(),
                State = (BrowserLoginState)result.State, Message = result.Message,
                AuthStatus = result.AuthStatus is { } status
                    ? new() { State = (AuthState)status.State, Message = status.Message } : null
            };
        }
        catch (ArgumentException e) { throw new RpcException(new Status(StatusCode.InvalidArgument, e.Message)); }
        catch (OperationCanceledException) { throw; }
        catch { throw new RpcException(new Status(StatusCode.Unavailable, "Could not start Mistral browser login. Try again.")); }
    }

    private async Task<AuthStatusReply> ExecuteAsync(Func<Task<ProviderAuthStatus>> action)
    {
        try
        {
            var result = await action();
            return new() { State = (AuthState)result.State, Message = result.Message };
        }
        catch (ArgumentException e)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, e.Message));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e)
        {
            // Do not log exception messages: secret-store errors can contain credentials.
            logger.LogError("Provider authentication failed ({ExceptionType}).", e.GetType().Name);
            throw new RpcException(new Status(StatusCode.Internal, "Could not access server credential storage."));
        }
    }
}
