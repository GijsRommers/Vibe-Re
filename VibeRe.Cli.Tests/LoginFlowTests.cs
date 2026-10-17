using Microsoft.Extensions.DependencyInjection;
using System.ComponentModel;
using Grpc.Core;
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

    [Fact]
    public async Task ServerSelectionControlsMistralActionsAndSurvivesRestart()
    {
        var connector = new SwitchingConnector();
        using var services = CreateServices(connector);
        await SeedServersAsync(services, connector);
        var model = services.GetRequiredService<ConnectionSettingsViewModel>();
        await model.InitializeAsync();
        Assert.True(model.IsSignedIn);

        model.ApiKey = "unsaved-key";
        await SelectServerAsync(model, model.Servers.Single(x => x.Name == "work"));
        Assert.True(model.NeedsLogin);
        Assert.False(model.IsSignedIn);
        Assert.Empty(model.ApiKey);
        Assert.Contains("work", model.LoginDescription);

        await model.LoginBrowserCommand.ExecuteAsync(null);
        await model.LogoutCommand.ExecuteAsync(null);
        Assert.Equal(1, connector.Clients["work"].BrowserLogins);
        Assert.Equal(1, connector.Clients["work"].Logouts);
        Assert.Equal(0, connector.Clients["local"].BrowserLogins);
        Assert.Equal(0, connector.Clients["local"].Logouts);

        var saved = await new ClientConnectionDatabase(_database).LoadAsync();
        Assert.Equal("work", saved.ActiveServer);
        var reopened = services.GetRequiredService<ConnectionSettingsViewModel>();
        await reopened.InitializeAsync();
        Assert.Equal("work", reopened.SelectedServer!.Name);
        Assert.True(reopened.NeedsLogin);
    }

    [Fact]
    public async Task OfflineServerRemainsSelectedWithoutFallingBackToLocal()
    {
        var connector = new SwitchingConnector();
        using var services = CreateServices(connector);
        await SeedServersAsync(services, connector);
        var model = services.GetRequiredService<ConnectionSettingsViewModel>();
        await model.InitializeAsync();
        connector.Offline.Add("work");
        connector.Requests.Clear();

        await SelectServerAsync(model, model.Servers.Single(x => x.Name == "work"));
        Assert.Equal("work", model.SelectedServer!.Name);
        Assert.False(model.IsSignedIn);
        Assert.Contains("Could not connect", model.Status);
        Assert.Equal(new[] { "work" }, connector.Requests);
        Assert.Equal("work", (await new ClientConnectionDatabase(_database).LoadAsync()).ActiveServer);
    }

    private ServiceProvider CreateServices(IServerConnector connector) =>
        new ServiceCollection().AddVibeReClient(_database)
            .AddSingleton(connector).AddSingleton<IBrowserLauncher>(new BrowserStub())
            .BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });

    private static async Task SeedServersAsync(ServiceProvider services, SwitchingConnector connector)
    {
        var connections = services.GetRequiredService<IServerConnectionManager>();
        var configuration = await connections.AddAsync("work", "https://vibe.example.com");
        foreach (var server in configuration.Servers)
            connector.Clients.Add(server.Name,
                new(server.Name == "local" ? AuthState.Authenticated : AuthState.SignedOut, server));
    }

    private static async Task SelectServerAsync(ConnectionSettingsViewModel model, ServerConnection server)
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        PropertyChangedEventHandler onChanged = (_, e) =>
        {
            if (e.PropertyName == nameof(model.IsBusy) && !model.IsBusy) completed.TrySetResult();
        };
        model.PropertyChanged += onChanged;
        try
        {
            model.SelectedServer = server;
            if (model.IsBusy) await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { model.PropertyChanged -= onChanged; }
    }

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

    private sealed class SwitchingConnector : IServerConnector
    {
        public Dictionary<string, ClientStub> Clients { get; } = [];
        public HashSet<string> Offline { get; } = [];
        public List<string> Requests { get; } = [];
        public Task<IVibeReClient> ConnectAsync(string? name = null, string? tokenOverride = null,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(name!);
            if (Offline.Contains(name!)) throw new RpcException(new Status(StatusCode.Unavailable, "Offline."));
            return Task.FromResult<IVibeReClient>(Clients[name!]);
        }
    }

    private sealed class ClientStub(AuthState state, ServerConnection? server = null) : IVibeReClient
    {
        public ServerConnection Server { get; } = server ?? new(Guid.NewGuid(), "local", "http://localhost:5067");
        public int BrowserLogins { get; private set; }
        public int Logouts { get; private set; }
        public Task<AuthStatusReply> GetStatusAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new AuthStatusReply { State = state, Message = "Test account status." });
        public async Task<BrowserLoginReply> LoginWithBrowserAsync(Func<BrowserLoginReply, Task> showBrowser,
            CancellationToken cancellationToken = default)
        {
            BrowserLogins++;
            await showBrowser(new() { SignInUrl = "https://console.mistral.ai/login", State = BrowserLoginState.Waiting });
            state = AuthState.Authenticated;
            return new() { State = BrowserLoginState.Completed, Message = "Connected.",
                AuthStatus = new() { State = AuthState.Authenticated, Message = "Connected." } };
        }
        public Task<AuthStatusReply> LoginAsync(string apiKey, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AuthStatusReply> LogoutAsync(CancellationToken cancellationToken = default)
        {
            Logouts++;
            state = AuthState.SignedOut;
            return GetStatusAsync(cancellationToken);
        }
        public Task<BrowserLoginReply> StartBrowserLoginAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<BrowserLoginReply> PollBrowserLoginAsync(string attemptId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<BrowserLoginReply> CancelBrowserLoginAsync(string attemptId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public void Dispose() { }
    }
}
