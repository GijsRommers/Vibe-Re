namespace VibeRe.Client.Application;

public static class ServerAddress
{
    public static string Normalize(string address)
    {
        if (!Uri.TryCreate(address.Trim(), UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") || uri.UserInfo.Length != 0 ||
            uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.AbsolutePath != "/")
            throw new ArgumentException("Enter a server origin, such as https://vibe.example.com.");
        if (uri.Scheme == "http" && !uri.IsLoopback)
            throw new ArgumentException("Remote servers require HTTPS.");
        return uri.GetLeftPart(UriPartial.Authority);
    }
}
