namespace VibeRe.Core.Auth;

/// <summary>Signs users in and out of providers and resolves the credential to use for each.</summary>
public sealed class AuthService
{
    private readonly Dictionary<string, IAuthProvider> _providers;
    private readonly ICredentialStore _store;
    private readonly Func<string, string?> _getEnvironmentVariable;

    public AuthService(
        IEnumerable<IAuthProvider> providers,
        ICredentialStore store,
        Func<string, string?>? getEnvironmentVariable = null)
    {
        _providers = providers.ToDictionary(p => p.Id, StringComparer.OrdinalIgnoreCase);
        _store = store;
        _getEnvironmentVariable = getEnvironmentVariable ?? Environment.GetEnvironmentVariable;
    }

    public IReadOnlyCollection<IAuthProvider> Providers => _providers.Values;

    public IAuthProvider GetProvider(string providerId) =>
        _providers.TryGetValue(providerId, out var provider)
            ? provider
            : throw new ArgumentException(
                $"Unknown provider '{providerId}'. Known providers: {string.Join(", ", _providers.Keys)}.");

    public async Task<AuthState> GetStateAsync(string providerId, CancellationToken cancellationToken = default)
    {
        var provider = GetProvider(providerId);
        var stored = await _store.GetAsync(provider.Id, cancellationToken);
        var source = ReadEnvironmentSecret(provider) is not null ? CredentialSource.Environment
            : stored is not null ? CredentialSource.Store
            : CredentialSource.None;
        return new AuthState(provider.Id, source, stored is not null, provider.ApiKeyEnvironmentVariable);
    }

    /// <summary>
    /// Returns the credential to use for <paramref name="providerId"/>: the provider's environment variable
    /// wins over the stored credential, so CI and one-off overrides don't require signing out.
    /// </summary>
    public async Task<Credential?> ResolveCredentialAsync(string providerId, CancellationToken cancellationToken = default)
    {
        var provider = GetProvider(providerId);
        if (ReadEnvironmentSecret(provider) is { } secret)
        {
            return new Credential(provider.Id, secret);
        }

        return await _store.GetAsync(provider.Id, cancellationToken);
    }

    /// <summary>Runs a sign-in flow and stores the resulting credential.</summary>
    /// <param name="flowId">The flow to run, or <c>null</c> for the provider's preferred flow.</param>
    public async Task<Credential> SignInAsync(
        string providerId,
        string? flowId,
        IAuthInteraction interaction,
        CancellationToken cancellationToken = default)
    {
        var provider = GetProvider(providerId);
        var flow = flowId is null
            ? provider.Flows[0]
            : provider.Flows.FirstOrDefault(f => string.Equals(f.Id, flowId, StringComparison.OrdinalIgnoreCase))
              ?? throw new ArgumentException(
                  $"{provider.DisplayName} does not support sign-in method '{flowId}'. " +
                  $"Supported: {string.Join(", ", provider.Flows.Select(f => f.Id))}.");

        var secret = await flow.AcquireSecretAsync(interaction, cancellationToken);
        var credential = new Credential(provider.Id, secret);
        await _store.SetAsync(credential, cancellationToken);
        return credential;
    }

    public Task<bool> SignOutAsync(string providerId, CancellationToken cancellationToken = default) =>
        _store.DeleteAsync(GetProvider(providerId).Id, cancellationToken);

    private string? ReadEnvironmentSecret(IAuthProvider provider) =>
        provider.ApiKeyEnvironmentVariable is { } name && _getEnvironmentVariable(name) is { Length: > 0 } value
            ? value
            : null;
}
