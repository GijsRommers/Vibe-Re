namespace VibeRe.Application.Providers.Auth;

public interface IAuthFlow
{
    string ProviderName { get; }
    Uri BaseUri { get; }
    Task<ProviderAuthStatus> ValidateAsync(string apiKey, CancellationToken cancellationToken = default);
}
