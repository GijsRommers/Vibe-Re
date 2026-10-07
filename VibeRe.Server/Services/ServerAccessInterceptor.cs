using System.Net;
using System.Security.Cryptography;
using System.Text;
using Grpc.Core;
using Grpc.Core.Interceptors;

namespace VibeRe.Server.Services;

// Without a token the development server accepts only local clients.
public sealed class ServerAccessInterceptor(IConfiguration configuration) : Interceptor
{
    public override Task<TResponse> UnaryServerHandler<TRequest, TResponse>(TRequest request,
        ServerCallContext context, UnaryServerMethod<TRequest, TResponse> continuation)
    {
        var expected = configuration["Server:AccessToken"];
        if (string.IsNullOrWhiteSpace(expected))
        {
            var remote = context.GetHttpContext().Connection.RemoteIpAddress;
            if (remote is null || !IPAddress.IsLoopback(remote))
                throw new RpcException(new Status(StatusCode.PermissionDenied, "Remote access requires a server access token."));
        }
        else
        {
            var supplied = context.RequestHeaders.GetValue("authorization");
            var valid = supplied is not null && CryptographicOperations.FixedTimeEquals(
                SHA256.HashData(Encoding.UTF8.GetBytes(supplied)),
                SHA256.HashData(Encoding.UTF8.GetBytes("Bearer " + expected)));
            if (!valid) throw new RpcException(new Status(StatusCode.Unauthenticated, "Enter the access token for this server."));
        }
        return continuation(request, context);
    }
}
