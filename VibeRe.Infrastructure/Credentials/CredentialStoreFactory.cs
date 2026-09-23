using VibeRe.Core.Auth;

namespace VibeRe.Infrastructure.Credentials;

/// <summary>Selects the credential store used by the application on this platform.</summary>
public static class CredentialStoreFactory
{
    /// <summary>Uses macOS Keychain on macOS and an owner-only file elsewhere.</summary>
    public static ICredentialStore CreateDefault() =>
        OperatingSystem.IsMacOS()
            ? new MacOsKeychainCredentialStore()
            : new FileCredentialStore(Path.Combine(VibeRePaths.Home, "credentials.json"));
}
