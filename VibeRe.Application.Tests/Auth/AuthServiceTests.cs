using VibeRe.Core.Auth;

namespace VibeRe.Core.Tests.Auth;

public class AuthServiceTests
{
    private readonly Dictionary<string, string> _env = [];
    private readonly InMemoryCredentialStore _store = new();
    private readonly AuthService _auth;

    public AuthServiceTests()
    {
        _auth = new AuthService([new FakeProvider()], _store, name => _env.GetValueOrDefault(name));
    }

    [Fact]
    public async Task Sign_in_stores_credential_and_sign_out_removes_it()
    {
        Assert.Equal(CredentialSource.None, (await _auth.GetStateAsync("fake")).ActiveSource);

        await _auth.SignInAsync("fake", ApiKeyAuthFlow.FlowId, new RecordingInteraction("  sk-stored \n"));

        Assert.Equal("sk-stored", (await _auth.ResolveCredentialAsync("fake"))?.Secret);
        Assert.Equal(CredentialSource.Store, (await _auth.GetStateAsync("fake")).ActiveSource);

        Assert.True(await _auth.SignOutAsync("fake"));
        Assert.False(await _auth.SignOutAsync("fake"));
        Assert.Null(await _auth.ResolveCredentialAsync("fake"));
    }

    [Fact]
    public async Task Environment_variable_takes_precedence_over_stored_credential()
    {
        await _store.SetAsync(new Credential("fake", "sk-stored"));
        _env["FAKE_API_KEY"] = "sk-env";

        var state = await _auth.GetStateAsync("FAKE");

        Assert.Equal(CredentialSource.Environment, state.ActiveSource);
        Assert.True(state.HasStoredCredential);
        Assert.Equal("sk-env", (await _auth.ResolveCredentialAsync("fake"))?.Secret);
    }

    [Fact]
    public async Task Empty_api_key_is_rejected_and_nothing_is_stored()
    {
        var error = await Assert.ThrowsAsync<AuthException>(
            () => _auth.SignInAsync("fake", null, new RecordingInteraction("   ")));

        Assert.Equal(AuthErrorCode.MissingSecret, error.Code);
        Assert.Null(await _store.GetAsync("fake"));
    }

    [Fact]
    public async Task Unknown_provider_or_method_is_rejected()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _auth.GetStateAsync("nope"));
        await Assert.ThrowsAsync<ArgumentException>(
            () => _auth.SignInAsync("fake", "carrier-pigeon", new RecordingInteraction("sk")));
    }

    private sealed class FakeProvider : IAuthProvider
    {
        public string Id => "fake";

        public string DisplayName => "Fake";

        public string ApiKeyEnvironmentVariable => "FAKE_API_KEY";

        public IReadOnlyList<IAuthFlow> Flows { get; } = [new ApiKeyAuthFlow("Fake")];

        public Task<AccountInfo?> GetAccountAsync(Credential credential, CancellationToken cancellationToken = default) =>
            Task.FromResult<AccountInfo?>(null);
    }

    private sealed class InMemoryCredentialStore : ICredentialStore
    {
        private readonly Dictionary<string, Credential> _credentials = [];

        public Task<Credential?> GetAsync(string providerId, CancellationToken cancellationToken = default) =>
            Task.FromResult(_credentials.GetValueOrDefault(providerId));

        public Task SetAsync(Credential credential, CancellationToken cancellationToken = default)
        {
            _credentials[credential.ProviderId] = credential;
            return Task.CompletedTask;
        }

        public Task<bool> DeleteAsync(string providerId, CancellationToken cancellationToken = default) =>
            Task.FromResult(_credentials.Remove(providerId));
    }
}
