using Grpc.Core;
using Grpc.Core.Interceptors;
using VibeRe.Application.Auth;

namespace VibeRe.Server.Services;

/// <summary>Turns auth and argument errors into gRPC statuses whose detail is safe to show the user.</summary>
public sealed class AuthExceptionInterceptor : Interceptor
{
    public override async Task<TResponse> UnaryServerHandler<TRequest, TResponse>(
        TRequest request,
        ServerCallContext context,
        UnaryServerMethod<TRequest, TResponse> continuation)
    {
        try
        {
            return await continuation(request, context);
        }
        catch (Exception ex) when (ToRpcException(ex) is { } rpcException)
        {
            throw rpcException;
        }
    }

    public override async Task DuplexStreamingServerHandler<TRequest, TResponse>(
        IAsyncStreamReader<TRequest> requestStream,
        IServerStreamWriter<TResponse> responseStream,
        ServerCallContext context,
        DuplexStreamingServerMethod<TRequest, TResponse> continuation)
    {
        try
        {
            await continuation(requestStream, responseStream, context);
        }
        catch (Exception ex) when (ToRpcException(ex) is { } rpcException)
        {
            throw rpcException;
        }
    }

    private static RpcException? ToRpcException(Exception exception) => exception switch
    {
        AuthException ex => new RpcException(new Status(ToStatusCode(ex.Code), ex.Message)),
        ArgumentException ex => new RpcException(new Status(StatusCode.InvalidArgument, ex.Message)),
        _ => null,
    };

    private static StatusCode ToStatusCode(AuthErrorCode code) => code switch
    {
        AuthErrorCode.MissingSecret => StatusCode.InvalidArgument,
        AuthErrorCode.Expired or AuthErrorCode.TimedOut => StatusCode.DeadlineExceeded,
        AuthErrorCode.Denied => StatusCode.PermissionDenied,
        AuthErrorCode.Unauthorized => StatusCode.Unauthenticated,
        AuthErrorCode.SubscriptionRequired => StatusCode.FailedPrecondition,
        _ => StatusCode.Unknown,
    };
}
