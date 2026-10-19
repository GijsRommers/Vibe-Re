using System.Net;

namespace VibeRe.Client.Application;

public static class ServerAddress
{
    public static string Normalize(string address)
    {
        if (!Uri.TryCreate(address.Trim(), UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") || uri.UserInfo.Length != 0 ||
            uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.AbsolutePath != "/")
            throw new ArgumentException("Enter a server origin, such as https://vibe.example.com.");
        if (uri.Scheme == "http" && !IsLoopback(address))
            throw new ArgumentException("Remote servers require HTTPS.");
        return uri.GetLeftPart(UriPartial.Authority);
    }
    public static bool IsLoopback(string address)
    {
        if (!Uri.TryCreate(address.Trim(), UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") || uri.UserInfo.Length != 0)
            return false;

        // Preserve the original host: Uri rewrites the DNS alias "loopback" to localhost,
        // but process listeners need a literal loopback address or the exact localhost name.
        var authority = address.Trim().Split("://", 2)[1].Split('/', '?', '#')[0];
        var host = authority.StartsWith('[')
            ? authority[1..authority.IndexOf(']')]
            : authority.Split(':')[0];
        return host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            (IPAddress.TryParse(host, out var ip) &&
             IPAddress.IsLoopback(ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip));
    }
}
