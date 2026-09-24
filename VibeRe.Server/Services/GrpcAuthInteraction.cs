using System.Threading.Channels;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using VibeRe.Application.Auth;
using VibeRe.Protocol;

namespace VibeRe.Server.Services;

/// <summary>Relays an <see cref="IAuthFlow"/>'s interaction to the remote client of a SignIn call.</summary>
internal sealed class GrpcAuthInteraction(
    IAsyncStreamReader<SignInRequest> requests,
    ChannelWriter<SignInResponse> responses) : IAuthInteraction
{
    public void ReportStatus(AuthStatus status) =>
        responses.TryWrite(new SignInResponse { Stage = ToStage(status) });

    public void OpenSignInUrl(Uri url, DateTimeOffset expiresAt) =>
        responses.TryWrite(new SignInResponse
        {
            OpenSignInUrl = new OpenSignInUrl
            {
                Url = url.AbsoluteUri,
                ExpiresAt = Timestamp.FromDateTimeOffset(expiresAt),
            },
        });

    public async Task<string?> PromptSecretAsync(string prompt, CancellationToken cancellationToken)
    {
        await responses.WriteAsync(new SignInResponse { SecretPrompt = new SecretPrompt { Prompt = prompt } }, cancellationToken);
        if (!await requests.MoveNext(cancellationToken)
            || requests.Current.MessageCase != SignInRequest.MessageOneofCase.Secret)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Expected a reply to the secret prompt."));
        }

        return requests.Current.Secret.HasSecret ? requests.Current.Secret.Secret : null;
    }

    private static SignInStage ToStage(AuthStatus status) => status switch
    {
        AuthStatus.OpeningBrowser => SignInStage.OpeningBrowser,
        AuthStatus.WaitingForUser => SignInStage.WaitingForUser,
        AuthStatus.Exchanging => SignInStage.Exchanging,
        AuthStatus.Completed => SignInStage.Completed,
        _ => SignInStage.Unspecified,
    };
}
