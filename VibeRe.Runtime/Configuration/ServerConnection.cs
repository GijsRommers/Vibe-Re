namespace VibeRe.Runtime.Configuration;

public sealed record ServerConnection(Guid Id, string Name, string Address, bool HasAccessToken = false, bool StartLocally = false)
{
    public override string ToString() => $"{Name} — {Address}";

    public static string ValidateName(string name)
    {
        name = name.Trim();
        if (name.Length is 0 or > 64 || name.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_')))
            throw new ArgumentException("Server names must contain letters, numbers, hyphens, or underscores.");
        return name;
    }
}
