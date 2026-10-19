using System.Net;

namespace VibeRe.Server.Services;

// One opt-in TLS-termination adapter, not a generic forwarded-header trust policy.
// It never uses Tailscale identity headers or the forwarded client IP to authorize a device.
public sealed class TailscaleServeMiddleware
{
    private readonly RequestDelegate _next;
    private readonly Uri? _origin;

    public TailscaleServeMiddleware(RequestDelegate next, IConfiguration configuration)
    {
        _next = next;
        _origin = ReadOrigin(configuration);
    }

    public Task InvokeAsync(HttpContext context)
    {
        if (!HasProxyHeaders(context.Request)) return _next(context);
        var request = context.Request;
        if (_origin is null || !LocalEndpoint.IsLoopback(context.Connection.RemoteIpAddress) ||
            request.Headers.ContainsKey("Forwarded") || request.Headers.ContainsKey("Tailscale-Funnel-Request") ||
            !MatchesAuthority(request.Host.Value, _origin) ||
            !SingleHeader(request, "X-Forwarded-Host", out var host) || !MatchesAuthority(host, _origin) ||
            !SingleHeader(request, "X-Forwarded-Proto", out var scheme) || scheme != "https" ||
            !SingleHeader(request, "X-Forwarded-For", out var address) || !IPAddress.TryParse(address, out _))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        }
        // Cookie security and exact-Origin checks must see the externally configured HTTPS origin.
        // Preserve the socket peer and proxy headers: ClientAccessPolicy must never call this local admin.
        request.Scheme = _origin.Scheme;
        request.Host = HostString.FromUriComponent(_origin);
        return _next(context);
    }

    public static bool HasProxyHeaders(HttpRequest request) => request.Headers.Keys.Any(name =>
        name.Equals("Forwarded", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("X-Forwarded-", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("Tailscale-", StringComparison.OrdinalIgnoreCase));

    private static Uri? ReadOrigin(IConfiguration configuration)
    {
        var configured = configuration["Server:TailscaleOrigin"];
        if (string.IsNullOrWhiteSpace(configured)) return null;
        if (!Uri.TryCreate(configured, UriKind.Absolute, out var origin) || origin.Scheme != "https" ||
            origin.HostNameType != UriHostNameType.Dns || !origin.IdnHost.EndsWith(".ts.net", StringComparison.OrdinalIgnoreCase) ||
            origin.UserInfo.Length != 0 || origin.AbsolutePath != "/" || origin.Query.Length != 0 ||
            origin.Fragment.Length != 0 || origin.Port <= 0)
            throw new InvalidOperationException("Server:TailscaleOrigin must be one HTTPS *.ts.net origin without a path or credentials.");

        // This opt-in trusts local processes only. Never combine it with a wildcard/LAN backend listener.
        var listeners = (configuration["urls"] ?? "http://localhost:5067").Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Concat(configuration.GetSection("Kestrel:Endpoints").GetChildren().Select(endpoint => endpoint["Url"] ?? ""));
        if (listeners.Any(listener => !IsLoopbackListener(listener)))
            throw new InvalidOperationException("Tailscale Serve requires every VibeRe backend listener to use a loopback address.");
        return origin;
    }

    private static bool IsLoopbackListener(string listener)
    {
        if (!Uri.TryCreate(listener, UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https") ||
            url.UserInfo.Length != 0 || url.AbsolutePath != "/" || url.Query.Length != 0 || url.Fragment.Length != 0)
            return false;
        // Uri normalizes the DNS alias "loopback" to "localhost", while Kestrel binds that alias
        // as a wildcard. Validate the original authority using the same literal hostname/IP rule.
        var separator = listener.IndexOf("://", StringComparison.Ordinal);
        if (separator < 0) return false;
        var host = new HostString(listener[(separator + 3)..].TrimEnd('/')).Host.Trim('[', ']');
        return LocalEndpoint.IsLoopbackHost(host);
    }

    private static bool SingleHeader(HttpRequest request, string name, out string value)
    {
        value = "";
        if (!request.Headers.TryGetValue(name, out var values) || values.Count != 1 ||
            string.IsNullOrEmpty(values[0]) || values[0]!.Contains(',')) return false;
        value = values[0]!;
        return true;
    }

    private static bool MatchesAuthority(string? authority, Uri expected) =>
        string.Equals(authority, expected.Authority, StringComparison.OrdinalIgnoreCase) ||
        (expected.IsDefaultPort && string.Equals(authority, expected.IdnHost + ":443", StringComparison.OrdinalIgnoreCase));

}
