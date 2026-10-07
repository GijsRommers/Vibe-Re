namespace VibeRe.Client.Configuration;

public sealed record ServerConnection(Guid Id, string Name, string Address, bool HasAccessToken = false, bool StartLocally = false)
{
    public override string ToString() => $"{Name} — {Address}";

    public static string NormalizeAddress(string address)
    {
        if (!Uri.TryCreate(address.Trim(), UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") || uri.UserInfo.Length != 0 ||
            uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.AbsolutePath != "/")
            throw new ArgumentException("Enter a server origin, such as https://vibe.example.com.");
        if (uri.Scheme == "http" && !uri.IsLoopback)
            throw new ArgumentException("Remote servers require HTTPS.");
        return uri.GetLeftPart(UriPartial.Authority);
    }

    public static string ValidateName(string name)
    {
        name = name.Trim();
        if (name.Length is 0 or > 64 || name.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_')))
            throw new ArgumentException("Server names must contain letters, numbers, hyphens, or underscores.");
        return name;
    }
}
