using System.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using VibeRe.Client.Application;
using VibeRe.Client.Application.Configuration;
using VibeRe.Client.Infrastructure;
using VibeRe.Client.Infrastructure.Adapters;

namespace VibeRe.Cli.Tests;

[CollectionDefinition("Pairing CLI", DisableParallelization = true)]
public sealed class PairingCliCollection;

[Collection("Pairing CLI")]
public sealed class PairingFlowTests : IDisposable
{
    private readonly string _database = Path.Combine(Path.GetTempPath(), $"vibere-pair-{Guid.NewGuid()}.db");

    [Fact]
    public async Task GuiPairingShowsApprovalChallengeSavesOnlyDeviceTokenAndRefreshesMistralStatus()
    {
        using var f = CreateFixture();
        var model = f.Services.GetRequiredService<ConnectionSettingsViewModel>();
        await model.InitializeAsync();
        model.PairingDeviceName = "My tablet";
        var checks = f.Connector.StatusChecks;
        f.Pairing.Handler = async (show, ct) =>
        {
            await show(new("ABCD1234", DateTimeOffset.UtcNow.AddMinutes(5)));
            Assert.Equal("ABCD1234", model.PairingApprovalCode);
            Assert.Contains("web UI locally", model.PairingInstructions);
            Assert.Contains("Expires", model.PairingInstructions);
            Assert.True(model.HasPairingChallenge);
            Assert.True(model.IsBusy);
            Assert.False(model.PairDeviceCommand.CanExecute(null));
            Assert.Empty(f.Tokens.Values);
            return "opaque-device-token";
        };

        await model.PairDeviceCommand.ExecuteAsync(null);

        Assert.Equal("http://localhost:5067", f.Pairing.Address);
        Assert.Equal("My tablet", f.Pairing.DeviceName);
        Assert.Equal("opaque-device-token", f.Tokens.Values[model.SelectedServer!.Id]);
        Assert.True(model.SelectedServer.HasAccessToken);
        Assert.Equal(checks + 1, f.Connector.StatusChecks);
        Assert.True(model.NeedsLogin);
        Assert.False(model.IsSignedIn);
        Assert.False(model.HasPairingChallenge);
        Assert.Empty(model.PairingInstructions);
        Assert.False(model.IsBusy);
        Assert.True(model.PairDeviceCommand.CanExecute(null));
        Assert.Empty(f.Browser.Opened);
        Assert.DoesNotContain("opaque-device-token", await File.ReadAllTextAsync(_database));
    }

    [Fact]
    public async Task CanceledPairingDoesNotSaveLateTokenAndCanBeRetried()
    {
        using var f = CreateFixture();
        var model = f.Services.GetRequiredService<ConnectionSettingsViewModel>();
        await model.InitializeAsync();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Pairing.Handler = async (show, _) =>
        {
            await show(new("ABCD1234", DateTimeOffset.UtcNow.AddMinutes(5)));
            started.SetResult();
            await finish.Task; // Deliberately ignore cancellation, exercising the application guard.
            return "late-token";
        };
        var pending = model.PairDeviceCommand.ExecuteAsync(null);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        model.CancelCommand.Execute(null);
        finish.SetResult();
        await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("Canceled.", model.Status);
        Assert.Empty(f.Tokens.Values);
        Assert.False(model.HasPairingChallenge);
        Assert.False(model.IsBusy);

        f.Pairing.Handler = (_, _) => Task.FromResult("retry-token");
        await model.PairDeviceCommand.ExecuteAsync(null);
        Assert.Equal("retry-token", Assert.Single(f.Tokens.Values).Value);
    }

    [Fact]
    public async Task ServerSelectionCancelsPairingAndRejectsLateTokenAndChallenge()
    {
        using var f = CreateFixture();
        await f.Connections.AddAsync("work", "https://work.example");
        var model = f.Services.GetRequiredService<ConnectionSettingsViewModel>();
        await model.InitializeAsync();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Pairing.Handler = async (show, _) =>
        {
            await show(new("OLD-CODE", DateTimeOffset.UtcNow.AddMinutes(5)));
            started.SetResult();
            await finish.Task;
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                show(new("LATE-CODE", DateTimeOffset.UtcNow.AddMinutes(5))));
            return "wrong-server-token";
        };
        var pending = model.PairDeviceCommand.ExecuteAsync(null);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await SelectAsync(model, model.Servers.Single(x => x.Name == "work"));
        finish.SetResult();
        await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("work", model.SelectedServer!.Name);
        Assert.Contains("work", model.Status);
        Assert.Empty(f.Tokens.Values);
        Assert.False(model.HasPairingChallenge);
        Assert.True(model.NeedsLogin);
    }

    [Fact]
    public async Task ChangingAddressInAnotherClientDuringPairingCannotPersistToken()
    {
        using var f = CreateFixture();
        var model = f.Services.GetRequiredService<ConnectionSettingsViewModel>();
        await model.InitializeAsync();
        f.Pairing.Handler = async (_, _) =>
        {
            await f.Connections.UpdateAddressAsync("local", "https://replacement.example");
            return "old-server-token";
        };
        await model.PairDeviceCommand.ExecuteAsync(null);
        Assert.Empty(f.Tokens.Values);
        Assert.Contains("server settings changed", model.Status);
        Assert.False((await f.Connections.LoadAsync()).Find().HasAccessToken);
        Assert.False(model.IsBusy);
    }

    [Fact]
    public async Task ExpiredPairingClearsChallengeAndSupportsRetry()
    {
        using var f = CreateFixture();
        var model = f.Services.GetRequiredService<ConnectionSettingsViewModel>();
        await model.InitializeAsync();
        f.Pairing.Handler = async (show, _) =>
        {
            await show(new("EXPIRED", DateTimeOffset.UtcNow));
            throw new ServerConnectionException("Pairing expired. Pair this device again to get a new code.");
        };
        await model.PairDeviceCommand.ExecuteAsync(null);
        Assert.Contains("Pairing expired", model.Status);
        Assert.False(model.HasPairingChallenge);
        Assert.True(model.PairDeviceCommand.CanExecute(null));
        Assert.Empty(f.Tokens.Values);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CliPairsNamedServerWithoutChangingSelectionOrExposingToken(bool suppliedDeviceName)
    {
        using var f = CreateFixture();
        await f.Connections.AddAsync("work", "https://work.example");
        f.Pairing.Handler = async (show, _) =>
        {
            await show(new("ABCD1234", DateTimeOffset.UtcNow.AddMinutes(5)));
            return "never-print-device-token";
        };
        var output = new StringWriter();
        var original = Console.Out;
        try
        {
            Console.SetOut(output);
            var args = suppliedDeviceName ? new[] { "servers", "pair", "work", "Laptop" } : ["servers", "pair", "work"];
            Assert.Equal(0, await new ServerCommands(f.Connections, f.Pairing, f.Connector)
                .ExecuteAsync(CliOptions.Parse(args), CancellationToken.None));
        }
        finally { Console.SetOut(original); }

        var config = await f.Connections.LoadAsync();
        Assert.Equal("local", config.ActiveServer);
        Assert.False(config.Find("local").HasAccessToken);
        Assert.True(config.Find("work").HasAccessToken);
        Assert.Equal("https://work.example", f.Pairing.Address);
        Assert.Equal(suppliedDeviceName ? "Laptop" : "CLI device", f.Pairing.DeviceName);
        Assert.Equal("never-print-device-token", f.Tokens.Values[config.Find("work").Id]);
        Assert.Equal(new[] { "work" }, f.Connector.Servers);
        Assert.Contains("Approval code: ABCD1234", output.ToString());
        Assert.Contains("web UI locally", output.ToString());
        Assert.Contains("Expires", output.ToString());
        Assert.Contains("auth login", output.ToString());
        Assert.DoesNotContain("never-print-device-token", output.ToString());
        Assert.Empty(f.Browser.Opened);
    }

    [Fact]
    public async Task CliRejectsCanceledOrAddressChangedPairingWithoutSavingToken()
    {
        using var f = CreateFixture();
        await f.Connections.LoadAsync();
        using var cancellation = new CancellationTokenSource();
        f.Pairing.Handler = (_, _) => { cancellation.Cancel(); return Task.FromResult("canceled-token"); };
        var commands = new ServerCommands(f.Connections, f.Pairing, f.Connector);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            commands.ExecuteAsync(CliOptions.Parse(["servers", "pair", "local"]), cancellation.Token));
        Assert.Empty(f.Tokens.Values);
        f.Pairing.Handler = async (_, _) =>
        {
            await f.Connections.UpdateAddressAsync("local", "https://replacement.example");
            return "stale-token";
        };
        await Assert.ThrowsAsync<ServerConnectionException>(() =>
            commands.ExecuteAsync(CliOptions.Parse(["servers", "pair", "local"]), CancellationToken.None));
        Assert.Empty(f.Tokens.Values);
        Assert.Empty(f.Connector.Servers);
    }

    private Fixture CreateFixture()
    {
        var database = new ClientConnectionDatabase(_database);
        var tokens = new Tokens();
        var connector = new Connector(database);
        var browser = new Browser();
        var pairing = new Pairing();
        var services = new ServiceCollection().AddVibeReClient(_database)
            .AddSingleton<IServerTokenStore>(tokens).AddSingleton<IServerConnector>(connector)
            .AddSingleton<IBrowserLauncher>(browser).AddSingleton<IDevicePairingClient>(pairing)
            .BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        return new(services, tokens, connector, browser, pairing);
    }

    private static async Task SelectAsync(ConnectionSettingsViewModel model, ServerConnection server)
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        PropertyChangedEventHandler changed = (_, e) =>
        {
            if (e.PropertyName == nameof(model.IsBusy) && !model.IsBusy) completed.TrySetResult();
        };
        model.PropertyChanged += changed;
        try { model.SelectedServer = server; if (model.IsBusy) await completed.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
        finally { model.PropertyChanged -= changed; }
    }

    public void Dispose() => File.Delete(_database);

    private sealed record Fixture(ServiceProvider Services, Tokens Tokens, Connector Connector, Browser Browser, Pairing Pairing) : IDisposable
    {
        public IServerConnectionManager Connections => Services.GetRequiredService<IServerConnectionManager>();
        public void Dispose() => Services.Dispose();
    }

    private sealed class Pairing : IDevicePairingClient
    {
        public Func<Func<DevicePairingChallenge, Task>, CancellationToken, Task<string>> Handler { get; set; } =
            (_, _) => Task.FromResult("device-token");
        public string? Address { get; private set; }
        public string? DeviceName { get; private set; }
        public Task<string> PairAsync(string serverAddress, string deviceName, Func<DevicePairingChallenge, Task> showChallenge,
            CancellationToken cancellationToken = default)
        { Address = serverAddress; DeviceName = deviceName; return Handler(showChallenge, cancellationToken); }
    }

    private sealed class Tokens : IServerTokenStore
    {
        public Dictionary<Guid, string> Values { get; } = [];
        public Task<string?> GetAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult(Values.GetValueOrDefault(id));
        public Task SetAsync(Guid id, string token, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); Values[id] = token; return Task.CompletedTask; }
        public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default) { Values.Remove(id); return Task.CompletedTask; }
    }

    private sealed class Browser : IBrowserLauncher
    {
        public List<Uri> Opened { get; } = [];
        public Task OpenAsync(Uri uri) { Opened.Add(uri); return Task.CompletedTask; }
    }

    private sealed class Connector(IClientConfigurationStore database) : IServerConnector
    {
        public List<string> Servers { get; } = [];
        public int StatusChecks { get; private set; }
        public async Task<(ServerConnection Server, IVibeReClient Client)> ConnectAsync(string? name = null, string? tokenOverride = null,
            CancellationToken cancellationToken = default)
        {
            var server = (await database.LoadAsync(cancellationToken)).Find(name);
            Servers.Add(server.Name);
            return (server, new Client(() => StatusChecks++));
        }
    }

    private sealed class Client(Action checkedStatus) : IVibeReClient
    {
        public Task<AuthStatus> GetStatusAsync(CancellationToken cancellationToken = default)
        { checkedStatus(); return Task.FromResult(new AuthStatus { State = AuthState.SignedOut, Message = "Not logged in." }); }
        public Task<AuthStatus> LoginAsync(string apiKey, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AuthStatus> LogoutAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<BrowserLoginResult> LoginWithBrowserAsync(Func<BrowserLoginResult, Task> showBrowser,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
