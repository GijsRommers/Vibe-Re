using VibeRe.Application.Auth;

namespace VibeRe.Infrastructure.Auth.Secrets;

internal interface ISecretStoreStrategy: ISecretStore
{
    bool IsSupported { get; }
}