using Microsoft.Extensions.DependencyInjection;
using VibeRe.Client;
using VibeRe.Client.Configuration;
using VibeRe.Protocol;

namespace VibeRe.Cli.Tests;

public sealed class LoginFlowTests : IDisposable
{
    private readonly string _database = Path.Combine(Path.GetTempPath(), $"vibere-launch-{Guid.NewGuid()}.db");

    [Theory]
    [InlineData(AuthState.SignedOut, 1)]
    [InlineData(AuthState.InvalidCredential, 1)]
    [InlineData(AuthState.Authenticated, 0)]
    [InlineData(AuthState.SubscriptionRequired, 0)]
    [InlineData(AuthState.Unavailable, 0)]
    public async Task CliLaunchOpensBrowserOnlyWhenLoginIsNeeded(AuthState state, int expectedLogins)
    {
        var client = new ClientStub(state);
        var browser = new BrowserStub();
        var commands = new AuthCommands(new ConnectorStub(client), browser);
        await commands.ExecuteAsync(CliOptions.Parse([]), CancellationToken.None);
        Assert.Equal(expectedLogins, client.BrowserLogins);
        Assert.Equal(expectedLogins, browser.Opened.Count);
    }

    [Fact]
    public async Task ExplicitStatusDoesNotStartBrowserLogin()
    {
        var client = new ClientStub(AuthState.SignedOut);
        var browser = new BrowserStub();
        var commands = new AuthCommands(new ConnectorStub(client), browser);
        Assert.Equal(1, await commands.ExecuteAsync(CliOptions.Parse(["auth", "status"]), CancellationToken.None));
        Assert.Equal(0, client.BrowserLogins);
        Assert.Empty(browser.Opened);
    }

    [Fact]
    public async Task GuiShowsInvitationAndLoginButtonUsesTheSharedBrowserFlow()
    {
        var client = new ClientStub(AuthState.SignedOut);
        var browser = new BrowserStub();
        using var services = new ServiceCollection().AddVibeReClient(_database)
            .AddSingleton<IServerConnector>(new ConnectorStub(client))
            .AddSingleton<IBrowserLauncher>(browser)
            .BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        var model = services.GetRequiredService<ConnectionSettingsViewModel>();

        await model.InitializeAsync();
        Assert.True(model.NeedsLogin);
        Assert.False(model.ShowApiKey);
        Assert.False(model.ShowServerSettings);
        Assert.False(model.HasBrowserUrl);
        Assert.Empty(browser.Opened);

        await model.LoginBrowserCommand.ExecuteAsync(null);
        Assert.Equal(1, client.BrowserLogins);
        Assert.Single(browser.Opened);
        Assert.False(model.NeedsLogin);
        Assert.True(model.IsSignedIn);
        Assert.False(model.IsBusy);
        Assert.False(model.HasBrowserUrl);
    }

    public void Dispose() => File.Delete(_database);

    private sealed class BrowserStub : IBrowserLauncher
    {
        public List<Uri> Opened { get; } = [];
        public Task OpenAsync(Uri uri) { Opened.Add(uri); return Task.CompletedTask; }
    }

    private sealed class ConnectorStub(IVibeReClient client) : IServerConnector
    {
        public Task<IVibeReClient> ConnectAsync(string? name = null, string? tokenOverride = null,
            CancellationToken cancellationToken = default) => Task.FromResult(client);
    }

    private sealed class ClientStub(AuthState state) : IVibeReClient
    {
        public ServerConnection Server { get; } = new(Guid.NewGuid(), "local", "http://localhost:5067");
        public int BrowserLogins { get; private set; }
        public Task<AuthStatusReply> GetStatusAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new AuthStatusReply { State = state, Message = "Test account status." });
        public async Task<BrowserLoginReply> LoginWithBrowserAsync(Func<BrowserLoginReply, Task> showBrowser,
            CancellationToken cancellationToken = default)
        {
            BrowserLogins++;
            await showBrowser(new() { SignInUrl = "https://console.mistral.ai/login", State = BrowserLoginState.Waiting });
            return new() { State = BrowserLoginState.Completed, Message = "Connected.",
                AuthStatus = new() { State = AuthState.Authenticated, Message = "Connected." } };
        }
        public Task<AuthStatusReply> LoginAsync(string apiKey, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AuthStatusReply> LogoutAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<BrowserLoginReply> StartBrowserLoginAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<BrowserLoginReply> PollBrowserLoginAsync(string attemptId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<BrowserLoginReply> CancelBrowserLoginAsync(string attemptId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public void Dispose() { }
    }
}
