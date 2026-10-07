namespace VibeRe.Application.Providers.Auth;

public interface IAuthService
{
    Task<ProviderAuthStatus> LoginAsync(string provider, string apiKey, CancellationToken cancellationToken = default);
    Task<ProviderAuthStatus> GetStatusAsync(string provider, CancellationToken cancellationToken = default);
    Task LogoutAsync(string provider, CancellationToken cancellationToken = default);
}
