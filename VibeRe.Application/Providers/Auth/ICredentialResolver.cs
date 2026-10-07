namespace VibeRe.Application.Providers.Auth;

public interface ICredentialResolver
{
    Task<string?> ResolveAsync(string provider, CancellationToken cancellationToken = default);
}
