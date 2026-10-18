using VibeRe.Client.Infrastructure;
using VibeRe.Client.Infrastructure.Adapters;
using Microsoft.Extensions.DependencyInjection;
using System.ComponentModel;
using VibeRe.Client.Application;
using VibeRe.Client.Application.Configuration;

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
        var commands = CreateCommands(new ConnectorStub(client), browser);
        await commands.ExecuteAsync(CliOptions.Parse([]), CancellationToken.None);
        Assert.Equal(expectedLogins, client.BrowserLogins);
        Assert.Equal(expectedLogins, browser.Opened.Count);
    }

    [Fact]
    public async Task ExplicitStatusDoesNotStartBrowserLogin()
    {
        var client = new ClientStub(AuthState.SignedOut);
        var browser = new BrowserStub();
        var commands = CreateCommands(new ConnectorStub(client), browser);
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

    [Fact]
    public async Task OptionalStartupSkipsConnectionButExplicitStatusStillConnects()
    {
        await new ClientConnectionDatabase(_database).UpdateAsync(c => Task.FromResult(c with { ServerRequired = false }));
        var client = new ClientStub(AuthState.SignedOut);
        var browser = new BrowserStub();
        var commands = CreateCommands(new ConnectorStub(client), browser);
        Assert.Equal(0, await commands.ExecuteAsync(CliOptions.Parse([]), CancellationToken.None));
        Assert.Equal(0, client.StatusChecks);
        Assert.Empty(browser.Opened);
        await commands.ExecuteAsync(CliOptions.Parse(["auth", "status"]), CancellationToken.None);
        Assert.Equal(1, client.StatusChecks);
    }

    [Fact]
    public async Task CliPrintsSignedOutAndVerificationUrlAndSurvivesBrowserFailure()
    {
        var output = new StringWriter();
        var original = Console.Out;
        try
        {
            Console.SetOut(output);
            var commands = CreateCommands(new ConnectorStub(new ClientStub(AuthState.SignedOut)), new BrowserStub(fail: true));
            Assert.Equal(0, await commands.ExecuteAsync(CliOptions.Parse([]), CancellationToken.None));
        }
        finally { Console.SetOut(original); }
        var text = output.ToString();
        Assert.Contains("Not logged in.", text);
        Assert.Contains("https://console.mistral.ai/login", text);
        Assert.Contains("Open the URL on any device", text);
        Assert.True(text.IndexOf("Not logged in.", StringComparison.Ordinal) < text.IndexOf("Starting Mistral login", StringComparison.Ordinal));
    }

    [Fact]
    public async Task OptionalGuiStartupWaitsForAnExplicitConnection()
    {
        var connector = new SwitchingConnector();
        using var services = CreateServices(connector);
        await SeedServersAsync(services, connector);
        await services.GetRequiredService<IServerConnectionManager>().SetServerRequiredAsync(false);
        var model = services.GetRequiredService<ConnectionSettingsViewModel>();
        await model.InitializeAsync();
        Assert.Empty(connector.Requests);
        Assert.False(model.ServerRequired);
        Assert.True(model.NeedsLogin);
        await model.RefreshCommand.ExecuteAsync(null);
        Assert.Equal(new[] { "local" }, connector.Requests);
        Assert.True(model.IsSignedIn);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelingGuiLoginClearsPendingStateAndAllowsRetry(bool wrapCancellation)
    {
        var connector = new SwitchingConnector();
        using var services = CreateServices(connector);
        await SeedServersAsync(services, connector);
        var model = services.GetRequiredService<ConnectionSettingsViewModel>();
        await model.InitializeAsync();
        await SelectServerAsync(model, model.Servers.Single(x => x.Name == "work"));
        var started = new TaskCompletionSource();
        connector.Clients["work"].LoginWait = async ct =>
        {
            started.SetResult();
            try { await Task.Delay(Timeout.Infinite, ct); }
            catch (OperationCanceledException error) when (wrapCancellation)
            { throw new OperationCanceledException("Canceled by caller.", error, ct); }
        };
        var login = model.LoginBrowserCommand.ExecuteAsync(null);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(model.HasBrowserUrl);
        Assert.False(model.LoginBrowserCommand.CanExecute(null));
        model.CancelPending();
        await login.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(model.IsBusy);
        Assert.False(model.HasBrowserUrl);
        Assert.True(model.NeedsLogin);
        Assert.Equal("Canceled.", model.Status);
        connector.Clients["work"].LoginWait = null;
        await model.LoginBrowserCommand.ExecuteAsync(null);
        Assert.True(model.IsSignedIn);
    }

    [Fact]
    public async Task FailedRefreshClearsThePreviousSignedInState()
    {
        var connector = new SwitchingConnector();
        using var services = CreateServices(connector);
        await SeedServersAsync(services, connector);
        var model = services.GetRequiredService<ConnectionSettingsViewModel>();
        await model.InitializeAsync();
        Assert.True(model.IsSignedIn);
        connector.Offline.Add("local");
        await model.RefreshCommand.ExecuteAsync(null);
        Assert.False(model.IsSignedIn);
        Assert.False(model.NeedsLogin);
        Assert.Contains("Could not connect", model.Status);
    }

    [Fact]
    public async Task LateLoginFromPreviousServerCannotOverwriteNewSelection()
    {
        var connector = new SwitchingConnector();
        using var services = CreateServices(connector);
        await SeedServersAsync(services, connector);
        var model = services.GetRequiredService<ConnectionSettingsViewModel>();
        await model.InitializeAsync();
        var started = new TaskCompletionSource();
        var finish = new TaskCompletionSource();
        connector.Clients["local"].LoginWait = _ => { started.SetResult(); return finish.Task; };
        var login = model.LoginBrowserCommand.ExecuteAsync(null);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await SelectServerAsync(model, model.Servers.Single(x => x.Name == "work"));
        finish.SetResult();
        await login.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("work", model.SelectedServer!.Name);
        Assert.Contains("work", model.Status);
        Assert.True(model.NeedsLogin);
        Assert.False(model.IsSignedIn);
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
        Assert.Contains("work", model.Status);

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

    [Theory]
    [InlineData("login", 1, 0)]
    [InlineData("logout", 0, 1)]
    public async Task OptionalStartupDoesNotSuppressExplicitAuthCommands(string verb, int logins, int logouts)
    {
        await new ClientConnectionDatabase(_database).UpdateAsync(c => Task.FromResult(c with { ServerRequired = false }));
        var client = new ClientStub(AuthState.SignedOut);
        var browser = new BrowserStub();
        var commands = CreateCommands(new ConnectorStub(client), browser);

        Assert.Equal(0, await commands.ExecuteAsync(CliOptions.Parse(["auth", verb]), CancellationToken.None));

        Assert.Equal(logins, client.BrowserLogins);
        Assert.Equal(logouts, client.Logouts);
        Assert.Equal(logouts, client.StatusChecks);
        Assert.True(client.Disposed);
    }

    [Theory]
    [InlineData("--local")]
    [InlineData("--token-stdin")]
    [InlineData("auth unknown")]
    [InlineData("auth status --method browser")]
    [InlineData("auth login --method unsupported")]
    [InlineData("auth login --method browser --api-key-stdin")]
    public async Task InvalidAuthOptionsDoNotConnect(string arguments)
    {
        var connector = new SwitchingConnector();
        var commands = CreateCommands(connector, new BrowserStub());

        await Assert.ThrowsAsync<ArgumentException>(() => commands.ExecuteAsync(CliOptions.Parse(arguments.Split(' ')), CancellationToken.None));

        Assert.Empty(connector.Requests);
    }

    [Fact]
    public async Task LateConnectionCannotStartLoginOnThePreviousServer()
    {
        var connector = new SwitchingConnector();
        using var services = CreateServices(connector);
        await SeedServersAsync(services, connector);
        var model = services.GetRequiredService<ConnectionSettingsViewModel>();
        await model.InitializeAsync();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        connector.ConnectWait = name =>
        {
            if (name != "local") return Task.CompletedTask;
            started.SetResult();
            return finish.Task;
        };

        var login = model.LoginBrowserCommand.ExecuteAsync(null);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await SelectServerAsync(model, model.Servers.Single(x => x.Name == "work"));
        finish.SetResult();
        await login.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(0, connector.Clients["local"].BrowserLogins);
        Assert.Equal("work", model.SelectedServer!.Name);
        Assert.Contains("work", model.Status);
        Assert.True(model.NeedsLogin);
        Assert.False(model.HasBrowserUrl);
    }

    private AuthCommands CreateCommands(IServerConnector connector, IBrowserLauncher browser) =>
        new(connector, new ClientStartup(new ClientConnectionDatabase(_database), connector), browser);

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

    private sealed class BrowserStub(bool fail = false) : IBrowserLauncher
    {
        public List<Uri> Opened { get; } = [];
        public Task OpenAsync(Uri uri) { Opened.Add(uri); if (fail) throw new InvalidOperationException("No browser"); return Task.CompletedTask; }
    }

    private sealed class ConnectorStub(IVibeReClient client) : IServerConnector
    {
        public Task<(ServerConnection Server, IVibeReClient Client)> ConnectAsync(string? name = null, string? tokenOverride = null,
            CancellationToken cancellationToken = default) => Task.FromResult((ClientConfiguration.Default().Find(), client));
    }

    private sealed class SwitchingConnector : IServerConnector
    {
        public Dictionary<string, ClientStub> Clients { get; } = [];
        public HashSet<string> Offline { get; } = [];
        public List<string> Requests { get; } = [];
        public Func<string, Task>? ConnectWait { get; set; }
        public async Task<(ServerConnection Server, IVibeReClient Client)> ConnectAsync(string? name = null, string? tokenOverride = null,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(name!);
            if (Offline.Contains(name!)) throw new ServerConnectionException("Could not connect to this server. Check its address and whether it is running.");
            if (ConnectWait is not null) await ConnectWait(name!);
            return (Clients[name!].Server, Clients[name!]);
        }
    }

    private sealed class ClientStub(AuthState state, ServerConnection? server = null) : IVibeReClient
    {
        public ServerConnection Server { get; } = server ?? new(Guid.NewGuid(), "local", "http://localhost:5067");
        public int BrowserLogins { get; private set; }
        public int Logouts { get; private set; }
        public int StatusChecks { get; private set; }
        public bool Disposed { get; private set; }
        public Func<CancellationToken, Task>? LoginWait { get; set; }
        public Task<AuthStatus> GetStatusAsync(CancellationToken cancellationToken = default)
        {
            StatusChecks++;
            return Task.FromResult(new AuthStatus { State = state, Message = "Test account status." });
        }
        public async Task<BrowserLoginResult> LoginWithBrowserAsync(Func<BrowserLoginResult, Task> showBrowser,
            CancellationToken cancellationToken = default)
        {
            BrowserLogins++;
            await showBrowser(new() { SignInUrl = "https://console.mistral.ai/login", State = BrowserLoginState.Waiting });
            if (LoginWait is not null) await LoginWait(cancellationToken);
            state = AuthState.Authenticated;
            return new() { State = BrowserLoginState.Completed, Message = "Connected.",
                AuthStatus = new() { State = AuthState.Authenticated, Message = "Connected." } };
        }
        public Task<AuthStatus> LoginAsync(string apiKey, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AuthStatus> LogoutAsync(CancellationToken cancellationToken = default)
        {
            Logouts++;
            state = AuthState.SignedOut;
            return GetStatusAsync(cancellationToken);
        }
        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
