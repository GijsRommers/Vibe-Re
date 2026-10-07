using VibeRe.Application.Providers.Profiles;
using VibeRe.Application.Secrets;

namespace VibeRe.Application.Providers.Auth;

public sealed class CredentialResolver(IProfileRepository profiles, ISecretStore secrets) : ICredentialResolver
{
    public async Task<string?> ResolveAsync(string provider, CancellationToken cancellationToken = default)
    {
        var profile = await profiles.FindAsync(provider, cancellationToken);
        return profile?.Credential is { } credential
            ? await secrets.GetAsync(CredentialSecret.Key(credential.Id), cancellationToken)
            : null;
    }
}
