using VibeRe.Application.Providers.Profiles;
using VibeRe.Application.Secrets;
using VibeRe.Domain.Credentials;
using VibeRe.Domain.Provider;

namespace VibeRe.Application.Providers.Auth;

// Singleton: serializes credential mutations so login/logout cannot race within the server.
public sealed class AuthService(
    IEnumerable<IAuthFlow> flows,
    IProfileRepository profiles,
    ISecretStore secrets,
    ICredentialResolver credentials) : IAuthService, IDisposable
{
    private readonly Dictionary<string, IAuthFlow> _flows = flows.ToDictionary(x => x.ProviderName, StringComparer.Ordinal);
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<ProviderAuthStatus> LoginAsync(string provider, string apiKey, CancellationToken cancellationToken = default)
    {
        var flow = GetFlow(provider);
        apiKey = apiKey.Trim();
        if (apiKey.Length is 0 or > 4096 || apiKey.Any(c => c < 33 || c > 126))
            throw new ArgumentException("Enter a valid API key.", nameof(apiKey));

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var status = await flow.ValidateAsync(apiKey, cancellationToken);
            if (status.State is not (ProviderAuthState.Authenticated or ProviderAuthState.SubscriptionRequired))
                return status;

            var profile = await profiles.FindAsync(provider, cancellationToken);
            var needsSave = profile?.Credential is null;
            profile ??= new ProviderProfile
            {
                Id = Guid.NewGuid(), ProviderIdentifier = provider,
                Name = provider, BaseUrl = flow.BaseUri.AbsoluteUri
            };
            profile.Credential ??= new Credential { Id = Guid.NewGuid(), Kind = CredentialKind.ApiKey };
            var key = CredentialSecret.Key(profile.Credential.Id);
            var previous = await secrets.GetAsync(key, cancellationToken);
            await secrets.SetAsync(key, apiKey, cancellationToken);
            try
            {
                if (needsSave)
                    await profiles.SaveAsync(profile, cancellationToken);
            }
            catch
            {
                // Rollback must complete even if the request was canceled.
                if (previous is null)
                    await secrets.DeleteAsync(key, CancellationToken.None);
                else
                    await secrets.SetAsync(key, previous, CancellationToken.None);
                throw;
            }

            return status with { Message = status.Message + " Credential saved on this server." };
        }
        finally { _gate.Release(); }
    }

    public async Task<ProviderAuthStatus> GetStatusAsync(string provider, CancellationToken cancellationToken = default)
    {
        var flow = GetFlow(provider);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var key = await credentials.ResolveAsync(provider, cancellationToken);
            return key is null
                ? new(ProviderAuthState.SignedOut, "This server is not logged in to Mistral.")
                : await flow.ValidateAsync(key, cancellationToken);
        }
        finally { _gate.Release(); }
    }

    public async Task LogoutAsync(string provider, CancellationToken cancellationToken = default)
    {
        GetFlow(provider);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var profile = await profiles.FindAsync(provider, cancellationToken);
            if (profile?.Credential is { } credential)
                await secrets.DeleteAsync(CredentialSecret.Key(credential.Id), cancellationToken);
        }
        finally { _gate.Release(); }
    }

    private IAuthFlow GetFlow(string provider) => _flows.TryGetValue(provider, out var flow)
        ? flow : throw new ArgumentException("Unsupported provider.", nameof(provider));

    public void Dispose() => _gate.Dispose();
}
