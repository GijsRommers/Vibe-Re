
namespace VibeRe.Infrastructure.Auth.Secrets;

public class LinuxSecretServiceSecretStoreStrategy: ISecretStoreStrategy
{
    public bool IsSupported { get; }
}