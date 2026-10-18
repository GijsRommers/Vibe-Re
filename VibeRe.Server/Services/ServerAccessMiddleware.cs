using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Net.Http.Headers;
using VibeRe.Contracts;

namespace VibeRe.Server.Services;

// Protect the complete SignalR transport, including negotiate, WebSockets, SSE and long polling.
public sealed class ServerAccessMiddleware(RequestDelegate next, IConfiguration configuration)
{
    private static readonly MediaTypeHeaderValue ServerSentEvents = new("text/event-stream");

    public async Task InvokeAsync(HttpContext context)
    {
        var request = context.Request;
        if (!request.Path.StartsWithSegments(ServerProtocol.AuthHubPath, StringComparison.OrdinalIgnoreCase))
        {
            await next(context);
            return;
        }

        var local = IsLoopback(context.Connection.RemoteIpAddress) && IsLoopbackHost(request.Host.Host);
        var expected = configuration["Server:AccessToken"];
        if (!string.IsNullOrWhiteSpace(expected))
        {
            var supplied = ReadToken(context);
            if (supplied is null || !CryptographicOperations.FixedTimeEquals(
                    SHA256.HashData(Encoding.UTF8.GetBytes(supplied)),
                    SHA256.HashData(Encoding.UTF8.GetBytes(expected))))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }
        }
        else if (!local)
        {
            // Checking Host as well as the peer address prevents browser DNS-rebinding access.
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        if ((!local && !request.IsHttps) || !HasSameOrigin(request))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        await next(context);
    }

    private static string? ReadToken(HttpContext context)
    {
        var request = context.Request;
        if (request.Headers.TryGetValue("Authorization", out var authorization))
        {
            // Never fall back to a query token when an invalid Authorization header was supplied.
            if (authorization.Count != 1 || authorization[0] is not { } header ||
                !header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return null;
            return header["Bearer ".Length..];
        }

        // Browser WebSockets/EventSource cannot set an Authorization header. Ordinary HTTP requests can.
        var isHubEndpoint = string.Equals(request.Path.Value?.TrimEnd('/'), ServerProtocol.AuthHubPath,
            StringComparison.OrdinalIgnoreCase);
        // HTTP/2 WebSockets use CONNECT rather than GET. Let the framework validate either handshake.
        if (!isHubEndpoint || (!context.WebSockets.IsWebSocketRequest &&
            !(HttpMethods.IsGet(request.Method) && AcceptsServerSentEvents(request)))) return null;
        return request.Query.TryGetValue("access_token", out var tokens) && tokens.Count == 1 ? tokens[0] : null;
    }

    private static bool AcceptsServerSentEvents(HttpRequest request)
    {
        // Match SignalR's transport selection exactly. A parameterized Accept value can fall back to
        // long polling, which must not gain query-token authentication just by resembling SSE.
        try { return request.GetTypedHeaders().Accept?.Contains(ServerSentEvents) == true; }
        catch (FormatException) { return false; }
    }

    private static bool HasSameOrigin(HttpRequest request)
    {
        if (!request.Headers.TryGetValue("Origin", out var values)) return true;
        if (values.Count != 1 || !Uri.TryCreate(values[0], UriKind.Absolute, out var origin) ||
            origin.Scheme is not ("http" or "https") || origin.UserInfo.Length != 0 ||
            origin.AbsolutePath != "/" || origin.Query.Length != 0 || origin.Fragment.Length != 0 ||
            !Uri.TryCreate($"{request.Scheme}://{request.Host}", UriKind.Absolute, out var server)) return false;
        return string.Equals(origin.Scheme, server.Scheme, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(origin.IdnHost, server.IdnHost, StringComparison.OrdinalIgnoreCase) &&
            origin.Port == server.Port;
    }

    private static bool IsLoopback(IPAddress? address) => address is not null &&
        IPAddress.IsLoopback(address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address);

    private static bool IsLoopbackHost(string host) =>
        string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) ||
        (IPAddress.TryParse(host.Trim('[', ']'), out var address) && IsLoopback(address));
}
