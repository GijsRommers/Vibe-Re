using System.Threading.Channels;
using Grpc.Core;
using VibeRe.Application.Auth;
using VibeRe.Protocol;
using Rpc = VibeRe.Protocol;

namespace VibeRe.Server.Services;

public sealed class AuthGrpcService(AuthService auth) : Rpc.Auth.AuthBase
{
    public override async Task<GetStatusResponse> GetStatus(GetStatusRequest request, ServerCallContext context)
    {
        var cancellationToken = context.CancellationToken;
        var provider = auth.GetProvider(request.ProviderId);
        var state = await auth.GetStateAsync(provider.Id, cancellationToken);
        var response = new GetStatusResponse
        {
            Provider = ToProviderInfo(provider),
            ActiveSource = ToProto(state.ActiveSource),
        };

        if (await auth.ResolveCredentialAsync(provider.Id, cancellationToken) is not { } credential)
        {
            response.AccountStatus = AccountStatus.SignedOut;
            return response;
        }

        try
        {
            response.AccountStatus = AccountStatus.Active;
            if (await provider.GetAccountAsync(credential, cancellationToken) is { } account)
            {
                response.Plan = account.Plan;
            }
        }
        catch (AuthException ex) when (ex.Code is AuthErrorCode.Unauthorized or AuthErrorCode.SubscriptionRequired)
        {
            response.AccountStatus = ex.Code == AuthErrorCode.Unauthorized
                ? AccountStatus.Rejected
                : AccountStatus.SubscriptionRequired;
            response.Message = ex.Message;
        }

        return response;
    }

    public override async Task SignIn(
        IAsyncStreamReader<SignInRequest> requestStream,
        IServerStreamWriter<SignInResponse> responseStream,
        ServerCallContext context)
    {
        var cancellationToken = context.CancellationToken;
        if (!await requestStream.MoveNext(cancellationToken)
            || requestStream.Current.MessageCase != SignInRequest.MessageOneofCase.Start)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Sign-in must begin with a start message."));
        }

        var start = requestStream.Current.Start;
        var provider = auth.GetProvider(start.ProviderId);

        // IAuthInteraction is synchronous, so queue its messages and write them from a single pump.
        var responses = Channel.CreateUnbounded<SignInResponse>(new UnboundedChannelOptions { SingleReader = true });
        var pump = PumpAsync(responses.Reader, responseStream, cancellationToken);
        try
        {
            await auth.SignInAsync(
                provider.Id,
                start.HasMethod ? start.Method : null,
                new GrpcAuthInteraction(requestStream, responses.Writer),
                cancellationToken);
            var state = await auth.GetStateAsync(provider.Id, cancellationToken);
            responses.Writer.TryWrite(new SignInResponse
            {
                Completed = new SignInCompleted
                {
                    Provider = ToProviderInfo(provider),
                    ActiveSource = ToProto(state.ActiveSource),
                },
            });
        }
        finally
        {
            responses.Writer.TryComplete();
            await pump;
        }
    }

    public override async Task<SignOutResponse> SignOut(SignOutRequest request, ServerCallContext context)
    {
        var provider = auth.GetProvider(request.ProviderId);
        var removed = await auth.SignOutAsync(provider.Id, context.CancellationToken);
        var state = await auth.GetStateAsync(provider.Id, context.CancellationToken);
        return new SignOutResponse
        {
            Provider = ToProviderInfo(provider),
            Removed = removed,
            ActiveSource = ToProto(state.ActiveSource),
        };
    }

    private static async Task PumpAsync(
        ChannelReader<SignInResponse> responses,
        IServerStreamWriter<SignInResponse> responseStream,
        CancellationToken cancellationToken)
    {
        await foreach (var response in responses.ReadAllAsync(cancellationToken))
        {
            await responseStream.WriteAsync(response, cancellationToken);
        }
    }

    private static ProviderInfo ToProviderInfo(IAuthProvider provider)
    {
        var info = new ProviderInfo { Id = provider.Id, DisplayName = provider.DisplayName };
        if (provider.ApiKeyEnvironmentVariable is { } environmentVariable)
        {
            info.ApiKeyEnvironmentVariable = environmentVariable;
        }

        return info;
    }

    private static Rpc.CredentialSource ToProto(Application.Auth.CredentialSource source) => source switch
    {
        Application.Auth.CredentialSource.Environment => Rpc.CredentialSource.Environment,
        Application.Auth.CredentialSource.Store => Rpc.CredentialSource.Store,
        _ => Rpc.CredentialSource.None,
    };
}
