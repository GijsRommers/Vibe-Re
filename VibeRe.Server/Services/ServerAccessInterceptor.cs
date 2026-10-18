using System.Net;
using System.Security.Cryptography;
using System.Text;
using Grpc.Core;
using Grpc.Core.Interceptors;

namespace VibeRe.Server.Services;

// Without a token the development server accepts only literal local endpoints.
public sealed class ServerAccessInterceptor(IConfiguration configuration) : Interceptor
{
    public override Task<TResponse> UnaryServerHandler<TRequest, TResponse>(TRequest request,
        ServerCallContext context, UnaryServerMethod<TRequest, TResponse> continuation)
    {
        var http = context.GetHttpContext();
        var local = IsLoopback(http.Connection.RemoteIpAddress) && IsLoopbackHost(http.Request.Host.Host);
        var expected = configuration["Server:AccessToken"];
        if (string.IsNullOrWhiteSpace(expected))
        {
            // Both the peer and Host must be local to prevent DNS-rebinding access.
            if (!local)
            {
                throw new RpcException(new Status(StatusCode.PermissionDenied, "Remote access requires a server access token."));
            }
        }
        else
        {
            var supplied = context.RequestHeaders.GetValue("authorization");
            var valid = supplied is not null && CryptographicOperations.FixedTimeEquals(
                SHA256.HashData(Encoding.UTF8.GetBytes(supplied)),
                SHA256.HashData(Encoding.UTF8.GetBytes("Bearer " + expected)));
            if (!valid)
            {
                throw new RpcException(new Status(StatusCode.Unauthenticated, "Enter the access token for this server."));
            }
        }

        if (!local && !http.Request.IsHttps)
        {
            throw new RpcException(new Status(StatusCode.PermissionDenied, "Remote access requires HTTPS."));
        }

        return continuation(request, context);
    }

    private static bool IsLoopback(IPAddress? address) => address is not null &&
        IPAddress.IsLoopback(address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address);

    private static bool IsLoopbackHost(string host) =>
        string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) ||
        (IPAddress.TryParse(host.Trim('[', ']'), out var address) && IsLoopback(address));
}
