using System.Security.Cryptography;
using System.Text;
using VibeRe.Domain.Access;
using VibeRe.Server.Application.Access;

namespace VibeRe.Server.Services;

public sealed record AuthorizedClient(string? Token = null, DeviceInfo? Device = null, bool IsAdministrator = false)
{
    public bool Authorized => IsAdministrator || Device is not null;
    public override string ToString() => $"AuthorizedClient {{ Device = {Device}, IsAdministrator = {IsAdministrator} }}";
}

// A single policy for HTTP endpoints, hub handshakes, and subsequent hub invocations.
public sealed class ClientAccessPolicy(IConfiguration configuration, IDeviceAccessService devices)
{
    // Origin-specific names prevent accidental overwrites, not cross-port disclosure: cookies are host-scoped.
    // HTTPS uses a host-only prefix to reject Domain-cookie injection. Trust every service on that hostname.
    public static string CookieName(HttpContext context)
    {
        var request = context.Request;
        var origin = $"{request.Scheme}://{request.Host.Host.ToLowerInvariant()}:{request.Host.Port ?? (request.IsHttps ? 443 : 80)}";
        return (request.IsHttps ? "__Host-vibere-device-" : "vibere-device-") + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(origin)))[..12].ToLowerInvariant();
    }
    public const string ItemKey = "VibeRe.ClientAccess";

    public async Task<AuthorizedClient> ResolveAsync(HttpContext context, CancellationToken cancellationToken = default)
    {
        var request = context.Request;
        // Durable credentials never belong in URLs, including WebSocket/SSE URLs.
        if (request.Query.ContainsKey("access_token")) return new();
        if (request.Headers.TryGetValue("Authorization", out var headers))
        {
            if (headers.Count != 1 || headers[0] is not { } header ||
                !header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return new();
            var token = header[7..];
            var expected = configuration["Server:AccessToken"];
            if (!string.IsNullOrWhiteSpace(expected) && Equal(token, expected))
                return new(token, IsAdministrator: true);
            return await FromDeviceAsync(token, cancellationToken);
        }
        // An invalid explicit credential must not fall back to local trust or another credential.
        if (request.Cookies.TryGetValue(CookieName(context), out var cookie))
            return await FromDeviceAsync(cookie, cancellationToken);
        return IsLocal(context) && string.IsNullOrWhiteSpace(configuration["Server:AccessToken"])
            ? new(IsAdministrator: true) : new();
    }

    // Hub callers retain this immutable handshake identity, not a disposed transport HttpContext.
    public async Task<AuthorizedClient> RevalidateAsync(AuthorizedClient client, CancellationToken cancellationToken)
    {
        if (client.Device is not null) return await FromDeviceAsync(client.Token!, cancellationToken);
        if (!client.IsAdministrator) return new();
        var expected = configuration["Server:AccessToken"];
        return client.Token is null
            ? string.IsNullOrWhiteSpace(expected) ? client : new()
            : !string.IsNullOrWhiteSpace(expected) && Equal(client.Token, expected) ? client : new();
    }

    private async Task<AuthorizedClient> FromDeviceAsync(string token, CancellationToken cancellationToken)
    {
        var device = await devices.ValidateSessionAsync(token, cancellationToken);
        return device is not null && device.Permissions.HasFlag(DevicePermissions.ProviderAccess)
            ? new(token, device, device.Permissions.HasFlag(DevicePermissions.ManageDevices)) : new();
    }

    public static bool CanManage(HttpContext context, AuthorizedClient client) => IsLocal(context) && client.IsAdministrator;
    public static bool IsSafeConnection(HttpContext context) =>
        (context.Request.IsHttps || IsLocal(context)) && HasSameOrigin(context.Request);

    public static bool IsLocal(HttpContext context) => !TailscaleServeMiddleware.HasProxyHeaders(context.Request) &&
        LocalEndpoint.IsLoopback(context.Connection.RemoteIpAddress) &&
        LocalEndpoint.IsLoopbackHost(context.Request.Host.Host);

    public static bool HasSameOrigin(HttpRequest request)
    {
        if (!request.Headers.TryGetValue("Origin", out var values)) return true;
        if (values.Count != 1 || !Uri.TryCreate(values[0], UriKind.Absolute, out var origin) ||
            origin.Scheme is not ("http" or "https") || origin.UserInfo.Length != 0 ||
            origin.AbsolutePath != "/" || origin.Query.Length != 0 || origin.Fragment.Length != 0 ||
            !Uri.TryCreate($"{request.Scheme}://{request.Host}", UriKind.Absolute, out var server)) return false;
        return string.Equals(origin.Scheme, server.Scheme, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(origin.IdnHost, server.IdnHost, StringComparison.OrdinalIgnoreCase) && origin.Port == server.Port;
    }

    private static bool Equal(string left, string right) => CryptographicOperations.FixedTimeEquals(
        SHA256.HashData(Encoding.UTF8.GetBytes(left)), SHA256.HashData(Encoding.UTF8.GetBytes(right)));
}
