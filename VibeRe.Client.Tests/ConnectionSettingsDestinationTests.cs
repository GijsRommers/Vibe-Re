using CommunityToolkit.Mvvm.Input;
using VibeRe.Client.Application;
using VibeRe.Client.Application.Configuration;

namespace VibeRe.Client.Tests;

public class ConnectionSettingsDestinationTests
{
    [Theory]
    [InlineData("api-key", "edited")]
    [InlineData("api-key", "recreated")]
    [InlineData("api-key", "origin-only")]
    [InlineData("browser", "edited")]
    [InlineData("browser", "recreated")]
    [InlineData("browser", "origin-only")]
    [InlineData("logout", "edited")]
    [InlineData("logout", "recreated")]
    [InlineData("logout", "origin-only")]
    [InlineData("refresh", "edited")]
    [InlineData("refresh", "recreated")]
    [InlineData("refresh", "origin-only")]
    public async Task StaleDisplayedProfileCannotAuthorizeActionsOnItsReplacement(string command, string change)
    {
        var fixture = new Fixture();
        await fixture.Model.InitializeAsync();
        var displayed = fixture.Model.SelectedServer!;
        fixture.Store.Current = new(displayed.Name, [displayed with
        {
            Id = change == "origin-only" ? displayed.Id : Guid.NewGuid(),
            Address = change == "recreated" ? displayed.Address : "https://replacement.example"
        }]);
        fixture.Model.ApiKey = "original-server-provider-key";

        await Command(fixture.Model, command).ExecuteAsync(null);

        var replacement = fixture.Connector.Clients.Last();
        Assert.Equal(0, replacement.Calls);
        Assert.True(replacement.Disposed);
        Assert.Empty(fixture.Browser.Opened);
        Assert.Contains("server settings changed", fixture.Model.Status, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(displayed, fixture.Model.SelectedServer);
        if (command == "api-key") Assert.Empty(fixture.Model.ApiKey);
    }

    [Theory]
    [InlineData("api-key")]
    [InlineData("browser")]
    [InlineData("logout")]
    [InlineData("refresh")]
    public async Task EquivalentOriginAndSameIdentityStillAllowTheRequestedAction(string command)
    {
        var fixture = new Fixture();
        await fixture.Model.InitializeAsync();
        var displayed = fixture.Model.SelectedServer!;
        fixture.Store.Current = new(displayed.Name, [displayed with { Address = "https://ORIGINAL.EXAMPLE:443/" }]);
        fixture.Model.ApiKey = "provider-key";

        await Command(fixture.Model, command).ExecuteAsync(null);

        var client = fixture.Connector.Clients.Last();
        Assert.Equal(1, client.Calls);
        Assert.True(client.Disposed);
        Assert.DoesNotContain("settings changed", fixture.Model.Status, StringComparison.OrdinalIgnoreCase);
        if (command == "api-key") Assert.Equal("provider-key", client.ReceivedKey);
        if (command == "browser") Assert.Single(fixture.Browser.Opened);
    }

    [Theory]
    [InlineData("api-key")]
    [InlineData("browser")]
    [InlineData("logout")]
    [InlineData("refresh")]
    public async Task LateConnectionAfterCancellationIsDisposedWithoutInvokingProvider(string command)
    {
        var fixture = new Fixture();
        await fixture.Model.InitializeAsync();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Connector.BeforeReturn = _ => gate.Task;
        fixture.Model.ApiKey = "provider-key";
        var pending = Command(fixture.Model, command).ExecuteAsync(null);

        fixture.Model.CancelPending();
        gate.SetResult();
        await pending;

        var client = fixture.Connector.Clients.Last();
        Assert.Equal(0, client.Calls);
        Assert.True(client.Disposed);
        Assert.Empty(fixture.Browser.Opened);
        Assert.Equal("Canceled.", fixture.Model.Status);
    }

    [Theory]
    [InlineData("api-key")]
    [InlineData("browser")]
    [InlineData("logout")]
    [InlineData("refresh")]
    public async Task LateConnectionAfterSelectionChangeCannotInvokeProviderOrOverwriteNewSelection(string command)
    {
        var fixture = new Fixture();
        var other = new ServerConnection(Guid.NewGuid(), "other", "https://other.example");
        fixture.Store.Current.Servers.Add(other);
        await fixture.Model.InitializeAsync();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Connector.BeforeReturn = name => name == "work" ? gate.Task : Task.CompletedTask;
        fixture.Model.ApiKey = "provider-key";
        var pending = Command(fixture.Model, command).ExecuteAsync(null);
        var oldClient = fixture.Connector.Clients.Last();

        fixture.Model.SelectedServer = other;
        var newStatus = fixture.Model.Status;
        gate.SetResult();
        await pending;

        Assert.Equal(0, oldClient.Calls);
        Assert.True(oldClient.Disposed);
        Assert.Empty(fixture.Browser.Opened);
        Assert.Equal(other, fixture.Model.SelectedServer);
        Assert.Equal(newStatus, fixture.Model.Status);
        Assert.StartsWith("other:", fixture.Model.Status);
        Assert.True(fixture.Model.NeedsLogin);
        Assert.False(fixture.Model.IsBusy);
    }

    [Fact]
    public async Task StartupCannotDisplayAReplacementServersAuthenticationAsTheOriginalProfile()
    {
        var fixture = new Fixture();
        var displayed = fixture.Store.Current.Find();
        fixture.Connector.ReplaceServer = server => server with { Id = Guid.NewGuid(), Address = "https://replacement.example" };

        await fixture.Model.InitializeAsync();

        Assert.Equal(displayed, fixture.Model.SelectedServer);
        Assert.Contains("server settings changed", fixture.Model.Status, StringComparison.OrdinalIgnoreCase);
        Assert.False(fixture.Model.NeedsLogin);
        Assert.False(fixture.Model.IsSignedIn);
        Assert.True(Assert.Single(fixture.Connector.Clients).Disposed);
    }

    private static IAsyncRelayCommand Command(ConnectionSettingsViewModel model, string command) => command switch
    {
        "api-key" => model.LoginApiKeyCommand,
        "browser" => model.LoginBrowserCommand,
        "logout" => model.LogoutCommand,
        "refresh" => model.RefreshCommand,
        _ => throw new ArgumentException("Unknown command.")
    };

    private sealed class Fixture
    {
        public Configuration Store { get; } = new();
        public Connector Connector { get; }
        public Browser Browser { get; } = new();
        public ConnectionSettingsViewModel Model { get; }

        public Fixture()
        {
            Connector = new(Store);
            Model = new(new ServerConnectionManager(Store, new Tokens()), Connector,
                new ClientStartup(Store, Connector), Browser);
        }
    }

    private sealed class Configuration : IClientConfigurationStore
    {
        public ClientConfiguration Current { get; set; } = new("work",
            [new(Guid.NewGuid(), "work", "https://original.example")]);
        public Task<ClientConfiguration> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(Current);
        public async Task<ClientConfiguration> UpdateAsync(Func<ClientConfiguration, Task<ClientConfiguration>> update,
            CancellationToken cancellationToken = default) => Current = await update(Current);
    }

    private sealed class Connector(Configuration configuration) : IServerConnector
    {
        public List<Client> Clients { get; } = [];
        public Func<string, Task>? BeforeReturn { get; set; }
        public Func<ServerConnection, ServerConnection>? ReplaceServer { get; set; }
        public async Task<(ServerConnection Server, IVibeReClient Client)> ConnectAsync(string? name = null,
            string? tokenOverride = null, CancellationToken cancellationToken = default)
        {
            var server = (await configuration.LoadAsync(cancellationToken)).Find(name);
            server = ReplaceServer?.Invoke(server) ?? server;
            var client = new Client();
            Clients.Add(client);
            if (BeforeReturn is { } wait) await wait(server.Name); // Deliberately ignores cancellation to exercise late completion.
            return (server, client);
        }
    }

    private sealed class Client : IVibeReClient
    {
        public int Calls { get; private set; }
        public bool Disposed { get; private set; }
        public string? ReceivedKey { get; private set; }
        private static AuthStatus SignedOut => new() { State = AuthState.SignedOut, Message = "Sign in." };
        public Task<AuthStatus> GetStatusAsync(CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(SignedOut);
        }
        public Task<AuthStatus> LoginAsync(string apiKey, CancellationToken cancellationToken = default)
        {
            Calls++;
            ReceivedKey = apiKey;
            return Task.FromResult(new AuthStatus { State = AuthState.Authenticated, Message = "Signed in." });
        }
        public Task<AuthStatus> LogoutAsync(CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(SignedOut);
        }
        public async Task<BrowserLoginResult> LoginWithBrowserAsync(Func<BrowserLoginResult, Task> showBrowser,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            await showBrowser(new() { SignInUrl = "https://console.mistral.ai/login", State = BrowserLoginState.Waiting });
            return new() { State = BrowserLoginState.Completed, Message = "Signed in.",
                AuthStatus = new() { State = AuthState.Authenticated } };
        }
        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class Browser : IBrowserLauncher
    {
        public List<Uri> Opened { get; } = [];
        public Task OpenAsync(Uri uri) { Opened.Add(uri); return Task.CompletedTask; }
    }

    private sealed class Tokens : IServerTokenStore
    {
        public Task<string?> GetAsync(Guid serverId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetAsync(Guid serverId, string token, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteAsync(Guid serverId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
