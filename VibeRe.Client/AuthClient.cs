using Grpc.Core;
using Rpc = VibeRe.Protocol;

namespace VibeRe.Client;

/// <summary>Signs the local user in and out of model providers through the server.</summary>
public sealed class AuthClient
{
    private readonly Rpc.Auth.AuthClient _grpc;
    private readonly Uri _address;

    internal AuthClient(Rpc.Auth.AuthClient grpc, Uri address)
    {
        _grpc = grpc;
        _address = address;
    }

    public async Task<AuthStatus> GetStatusAsync(string providerId, CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await _grpc.GetStatusAsync(
                new Rpc.GetStatusRequest { ProviderId = providerId }, cancellationToken: cancellationToken);
            return new AuthStatus(
                ToProviderInfo(response.Provider),
                ToCredentialSource(response.ActiveSource),
                response.AccountStatus switch
                {
                    Rpc.AccountStatus.Active => AccountStatus.Active,
                    Rpc.AccountStatus.Rejected => AccountStatus.Rejected,
                    Rpc.AccountStatus.SubscriptionRequired => AccountStatus.SubscriptionRequired,
                    _ => AccountStatus.SignedOut,
                },
                response.HasPlan ? response.Plan : null,
                response.HasMessage ? response.Message : null);
        }
        catch (RpcException ex)
        {
            throw Translate(ex, cancellationToken);
        }
    }

    /// <summary>Runs a sign-in on the server, relaying its prompts to <paramref name="interaction"/>.</summary>
    /// <param name="method">The sign-in method, or <c>null</c> for the provider's preferred one.</param>
    public async Task<SignInResult> SignInAsync(
        string providerId,
        string? method,
        ISignInInteraction interaction,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var call = _grpc.SignIn(cancellationToken: cancellationToken);
            var start = new Rpc.StartSignIn { ProviderId = providerId };
            if (method is not null)
            {
                start.Method = method;
            }

            await TryWriteAsync(call, new Rpc.SignInRequest { Start = start }, cancellationToken);

            SignInResult? result = null;
            await foreach (var response in call.ResponseStream.ReadAllAsync(cancellationToken))
            {
                switch (response.MessageCase)
                {
                    case Rpc.SignInResponse.MessageOneofCase.Stage when ToStage(response.Stage) is { } stage:
                        interaction.ReportStage(stage);
                        break;
                    case Rpc.SignInResponse.MessageOneofCase.OpenSignInUrl:
                        interaction.OpenSignInUrl(
                            new Uri(response.OpenSignInUrl.Url),
                            response.OpenSignInUrl.ExpiresAt.ToDateTimeOffset());
                        break;
                    case Rpc.SignInResponse.MessageOneofCase.SecretPrompt:
                        var secret = await interaction.PromptSecretAsync(response.SecretPrompt.Prompt, cancellationToken);
                        var reply = new Rpc.SecretReply();
                        if (secret is not null)
                        {
                            reply.Secret = secret;
                        }

                        await TryWriteAsync(call, new Rpc.SignInRequest { Secret = reply }, cancellationToken);
                        break;
                    case Rpc.SignInResponse.MessageOneofCase.Completed:
                        result = new SignInResult(
                            ToProviderInfo(response.Completed.Provider),
                            ToCredentialSource(response.Completed.ActiveSource));
                        await call.RequestStream.CompleteAsync();
                        break;
                }
            }

            return result ?? throw new VibeReClientException("The server ended sign-in without completing it.");
        }
        catch (RpcException ex)
        {
            throw Translate(ex, cancellationToken);
        }
    }

    public async Task<SignOutResult> SignOutAsync(string providerId, CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await _grpc.SignOutAsync(
                new Rpc.SignOutRequest { ProviderId = providerId }, cancellationToken: cancellationToken);
            return new SignOutResult(
                ToProviderInfo(response.Provider),
                response.Removed,
                ToCredentialSource(response.ActiveSource));
        }
        catch (RpcException ex)
        {
            throw Translate(ex, cancellationToken);
        }
    }

    private static async Task TryWriteAsync(
        AsyncDuplexStreamingCall<Rpc.SignInRequest, Rpc.SignInResponse> call,
        Rpc.SignInRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            await call.RequestStream.WriteAsync(request, cancellationToken);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // A write only fails once the call has; reading the response stream surfaces the real status.
        }
    }

    private Exception Translate(RpcException exception, CancellationToken cancellationToken) => exception.StatusCode switch
    {
        StatusCode.Cancelled when cancellationToken.IsCancellationRequested =>
            new OperationCanceledException(exception.Status.Detail, exception, cancellationToken),
        StatusCode.Unavailable => new ServerUnavailableException(_address, exception),
        _ => new VibeReClientException(exception.Status.Detail, exception),
    };

    private static ProviderInfo ToProviderInfo(Rpc.ProviderInfo provider) => new(
        provider.Id,
        provider.DisplayName,
        provider.HasApiKeyEnvironmentVariable ? provider.ApiKeyEnvironmentVariable : null);

    private static CredentialSource ToCredentialSource(Rpc.CredentialSource source) => source switch
    {
        Rpc.CredentialSource.Environment => CredentialSource.Environment,
        Rpc.CredentialSource.Store => CredentialSource.Store,
        _ => CredentialSource.None,
    };

    private static SignInStage? ToStage(Rpc.SignInStage stage) => stage switch
    {
        Rpc.SignInStage.OpeningBrowser => SignInStage.OpeningBrowser,
        Rpc.SignInStage.WaitingForUser => SignInStage.WaitingForUser,
        Rpc.SignInStage.Exchanging => SignInStage.Exchanging,
        Rpc.SignInStage.Completed => SignInStage.Completed,
        _ => null,
    };
}
