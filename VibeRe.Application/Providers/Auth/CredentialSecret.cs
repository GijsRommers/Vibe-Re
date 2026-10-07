namespace VibeRe.Application.Providers.Auth;

public static class CredentialSecret
{
    public static string Key(Guid id) => $"provider-credential/{id:D}";
}
