using VibeRe.Application.Auth;

namespace VibeRe.Infrastructure.Auth.Secrets;

public class MacOsKeychainSecretStoreStrategy: ISecretStoreStrategy
{
    public bool IsSupported { get; }
}