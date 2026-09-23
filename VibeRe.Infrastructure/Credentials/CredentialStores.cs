using VibeRe.Core.Auth;

namespace VibeRe.Infrastructure.Credentials;

public static class CredentialStores
{
    /// <summary>The OS keychain where supported, otherwise an owner-only file under <see cref="VibeRePaths.Home"/>.</summary>
    public static ICredentialStore CreateDefault() =>
        OperatingSystem.IsMacOS()
            ? new MacOsKeychainCredentialStore()
            : new FileCredentialStore(Path.Combine(VibeRePaths.Home, "credentials.json"));
}
