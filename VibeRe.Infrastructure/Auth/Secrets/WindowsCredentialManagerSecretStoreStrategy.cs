using VibeRe.Application.Auth;

namespace VibeRe.Infrastructure.Auth.Secrets;

public class WindowsCredentialManagerSecretStoreStrategy: ISecretStoreStrategy
{
    public bool IsSupported { get; }
}