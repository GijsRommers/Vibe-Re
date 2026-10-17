using VibeRe.Application.Providers.Profiles;
using VibeRe.Application.Secrets;
using VibeRe.Domain.Credentials;
using VibeRe.Domain.Provider;

namespace VibeRe.Application.Providers.Auth;

public sealed class ProviderCredentialStore(IProfileRepository profiles, ISecretStore secrets) : IProviderCredentialStore
{
    public async Task<string?> ResolveAsync(string provider, CancellationToken cancellationToken = default)
    {
        var profile = await profiles.FindAsync(provider, cancellationToken);
        return profile?.Credential is { } credential
            ? await secrets.GetAsync(CredentialSecret.Key(credential.Id), cancellationToken) : null;
    }

    public async Task SaveAsync(string provider, Uri baseUri, string apiKey, CancellationToken cancellationToken = default)
    {
        var profile = await profiles.FindAsync(provider, cancellationToken);
        var needsSave = profile?.Credential is null;
        profile ??= new ProviderProfile
        {
            Id = Guid.NewGuid(), ProviderIdentifier = provider, Name = provider, BaseUrl = baseUri.AbsoluteUri
        };
        profile.Credential ??= new Credential { Id = Guid.NewGuid(), Kind = CredentialKind.ApiKey };
        var key = CredentialSecret.Key(profile.Credential.Id);
        var previous = await secrets.GetAsync(key, cancellationToken);
        await secrets.SetAsync(key, apiKey, cancellationToken);
        try
        {
            if (needsSave) await profiles.SaveAsync(profile, cancellationToken);
        }
        catch
        {
            // Restore the old secret even when the original request was canceled.
            if (previous is null) await secrets.DeleteAsync(key, CancellationToken.None);
            else await secrets.SetAsync(key, previous, CancellationToken.None);
            throw;
        }
    }

    public async Task DeleteAsync(string provider, CancellationToken cancellationToken = default)
    {
        var profile = await profiles.FindAsync(provider, cancellationToken);
        if (profile?.Credential is { } credential)
            await secrets.DeleteAsync(CredentialSecret.Key(credential.Id), cancellationToken);
    }
}
