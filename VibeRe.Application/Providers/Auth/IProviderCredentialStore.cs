namespace VibeRe.Application.Providers.Auth;

public interface IProviderCredentialStore : ICredentialResolver
{
    Task SaveAsync(string provider, Uri baseUri, string apiKey, CancellationToken cancellationToken = default);
    Task DeleteAsync(string provider, CancellationToken cancellationToken = default);
}
