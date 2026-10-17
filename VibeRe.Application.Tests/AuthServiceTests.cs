using VibeRe.Application.Providers.Auth;
using VibeRe.Application.Providers.Profiles;
using VibeRe.Application.Secrets;
using VibeRe.Domain.Provider;

namespace VibeRe.Application.Tests;

public class AuthServiceTests
{
    [Fact]
    public async Task LoginPersistsServerSecretAndLogoutRemovesIt()
    {
        var fixture = new Fixture();
        Assert.Equal(ProviderAuthState.SignedOut, (await fixture.Auth.GetStatusAsync("mistral")).State);
        Assert.Equal(ProviderAuthState.Authenticated, (await fixture.Auth.LoginAsync("mistral", "valid-key")).State);
        Assert.Equal("valid-key", await fixture.Resolver.ResolveAsync("mistral"));
        Assert.NotNull(fixture.Profiles.Profile?.Credential);
        await fixture.Auth.LogoutAsync("mistral");
        Assert.Null(await fixture.Resolver.ResolveAsync("mistral"));
        Assert.Equal(ProviderAuthState.SignedOut, (await fixture.Auth.GetStatusAsync("mistral")).State);
        await fixture.Auth.LogoutAsync("mistral");
    }

    [Theory]
    [InlineData(ProviderAuthState.InvalidCredential)]
    [InlineData(ProviderAuthState.Unavailable)]
    public async Task FailedLoginDoesNotReplaceExistingCredential(ProviderAuthState state)
    {
        var fixture = new Fixture();
        await fixture.Auth.LoginAsync("mistral", "old-key");
        fixture.Flow.State = state;
        Assert.Equal(state, (await fixture.Auth.LoginAsync("mistral", "bad-key")).State);
        Assert.Equal("old-key", await fixture.Resolver.ResolveAsync("mistral"));
    }

    [Fact]
    public async Task BillingRequiredStoresRecognizedKeyAndRemainsDistinctFromSignedOut()
    {
        var fixture = new Fixture();
        fixture.Flow.State = ProviderAuthState.SubscriptionRequired;
        await fixture.Auth.LoginAsync("mistral", "billing-key");
        Assert.Equal("billing-key", await fixture.Resolver.ResolveAsync("mistral"));
        Assert.Equal(ProviderAuthState.SubscriptionRequired, (await fixture.Auth.GetStatusAsync("mistral")).State);
    }

    [Fact]
    public async Task RepositoryFailureRollsBackNewSecret()
    {
        var fixture = new Fixture();
        fixture.Profiles.FailSave = true;
        await Assert.ThrowsAsync<IOException>(() => fixture.Auth.LoginAsync("mistral", "valid-key"));
        Assert.Empty(fixture.Secrets.Values);
    }

    [Theory]
    [InlineData("")]
    [InlineData("key\r\nheader")]
    [InlineData("key with spaces")]
    public async Task MalformedKeyIsRejectedBeforeProviderRequest(string key)
    {
        var fixture = new Fixture();
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Auth.LoginAsync("mistral", key));
        Assert.Equal(0, fixture.Flow.Calls);
        Assert.Empty(fixture.Secrets.Values);
    }

    internal sealed class Fixture
    {
        public Profiles Profiles { get; } = new();
        public Secrets Secrets { get; } = new();
        public Flow Flow { get; } = new();
        public ProviderCredentialStore Resolver { get; }
        public AuthService Auth { get; }
        public Fixture()
        {
            Resolver = new(Profiles, Secrets);
            Auth = new([Flow], Resolver);
        }
    }

    internal sealed class Profiles : IProfileRepository
    {
        public ProviderProfile? Profile { get; private set; }
        public bool FailSave { get; set; }
        public Task<ProviderProfile?> FindAsync(string provider, CancellationToken cancellationToken = default) => Task.FromResult(Profile);
        public Task SaveAsync(ProviderProfile profile, CancellationToken cancellationToken = default)
        {
            if (FailSave) throw new IOException("Simulated database failure.");
            Profile = profile;
            return Task.CompletedTask;
        }
    }

    internal sealed class Secrets : ISecretStore
    {
        public Dictionary<string, string> Values { get; } = [];
        public Task<string?> GetAsync(string key, CancellationToken cancellationToken) => Task.FromResult(Values.GetValueOrDefault(key));
        public Task SetAsync(string key, string value, CancellationToken cancellationToken) { Values[key] = value; return Task.CompletedTask; }
        public Task DeleteAsync(string key, CancellationToken cancellationToken) { Values.Remove(key); return Task.CompletedTask; }
    }

    internal sealed class Flow : IAuthFlow
    {
        public string ProviderName => "mistral";
        public Uri BaseUri => new("https://api.mistral.ai/");
        public ProviderAuthState State { get; set; } = ProviderAuthState.Authenticated;
        public int Calls { get; private set; }
        public Task<ProviderAuthStatus> ValidateAsync(string apiKey, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new ProviderAuthStatus(State, State.ToString()));
        }
    }
}
