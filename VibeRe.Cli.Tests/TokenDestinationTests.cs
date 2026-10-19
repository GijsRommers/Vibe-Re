using System.ComponentModel;
using VibeRe.Client.Application;
using VibeRe.Client.Application.Configuration;
using VibeRe.Client.Infrastructure.Adapters;

namespace VibeRe.Cli.Tests;

[CollectionDefinition("Token CLI", DisableParallelization = true)]
public sealed class TokenCliCollection;

[Collection("Token CLI")]
public sealed class TokenDestinationTests : IDisposable
{
    private readonly string _database = Path.Combine(Path.GetTempPath(), $"vibere-token-prompt-{Guid.NewGuid()}.db");
    private readonly Tokens _tokens = new();
    private ClientConnectionDatabase Store => new(_database);
    private ServerConnectionManager Manager => new(Store, _tokens);

    [Theory]
    [InlineData("address")]
    [InlineData("recreate")]
    [InlineData("same-id-address")]
    public async Task GuiCannotSaveEnteredTokenAfterAnotherClientChangesItsDestination(string change)
    {
        var original = (await Manager.AddAsync("work", "https://original.example")).Find("work");
        await Manager.SelectAsync("work");
        var model = CreateModel();
        await model.InitializeAsync();
        model.ServerToken = "token-for-original";
        await ChangeDestinationAsync(change, original);

        await model.SaveTokenCommand.ExecuteAsync(null);

        Assert.Empty(_tokens.Values);
        Assert.False((await Manager.LoadAsync()).Find("work").HasAccessToken);
        Assert.Contains("server settings changed", model.Status);
        Assert.Empty(model.ServerToken);
        Assert.False(model.IsBusy);
    }

    [Fact]
    public async Task GuiCapturesTheSelectedProfileBeforeClearingTheSecretField()
    {
        await Manager.AddAsync("work", "https://work.example");
        var model = CreateModel();
        await model.InitializeAsync();
        var original = model.SelectedServer!;
        model.ServerToken = "local-token";
        PropertyChangedEventHandler changed = (_, e) =>
        {
            if (e.PropertyName == nameof(model.ServerToken) && model.ServerToken.Length == 0)
                model.SelectedServer = model.Servers.Single(server => server.Name == "work");
        };
        model.PropertyChanged += changed;
        try
        {
            await model.SaveTokenCommand.ExecuteAsync(null);
        }
        finally
        {
            model.PropertyChanged -= changed;
        }

        var saved = await Manager.LoadAsync();
        Assert.False(saved.Find("work").HasAccessToken);
        Assert.True(saved.Find(original.Name).HasAccessToken);
        Assert.Equal("local-token", _tokens.Values[saved.Find(original.Name).Id]);
    }

    [Theory]
    [InlineData("address")]
    [InlineData("recreate")]
    [InlineData("same-id-address")]
    public async Task CliBindsTheDestinationBeforeWaitingForTokenInput(string change)
    {
        var original = (await Manager.AddAsync("work", "https://original.example")).Find("work");
        var input = new PausedInput();
        var previous = Console.In;
        Task<int>? pending = null;
        try
        {
            Console.SetIn(input);
            pending = Task.Run(() => new ServerCommands(Manager).ExecuteAsync(
                CliOptions.Parse(["servers", "token", "work", "--token-stdin"]), CancellationToken.None));
            await input.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await ChangeDestinationAsync(change, original);
            input.Resume.SetResult("token-for-original");

            await Assert.ThrowsAsync<ServerConnectionException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));

            Assert.Empty(_tokens.Values);
            Assert.False((await Manager.LoadAsync()).Find("work").HasAccessToken);
            Assert.Equal("local", (await Manager.LoadAsync()).ActiveServer);
        }
        finally
        {
            input.Resume.TrySetResult("");
            if (pending is not null)
            {
                try { await pending.WaitAsync(TimeSpan.FromSeconds(5)); }
                catch { /* The assertion above owns the expected failure. */ }
            }
            Console.SetIn(previous);
        }
    }

    [Fact]
    public async Task CliSavesTokenForThePromptedProfileWithoutChangingSelection()
    {
        var original = (await Manager.AddAsync("work", "https://work.example")).Find("work");
        var previous = Console.In;
        try
        {
            Console.SetIn(new StringReader("work-token\n"));
            Assert.Equal(0, await new ServerCommands(Manager).ExecuteAsync(
                CliOptions.Parse(["servers", "token", "work", "--token-stdin"]), CancellationToken.None));
        }
        finally
        {
            Console.SetIn(previous);
        }

        var saved = await Manager.LoadAsync();
        Assert.Equal("local", saved.ActiveServer);
        Assert.NotEqual(original.Id, saved.Find("work").Id);
        Assert.Equal("work-token", _tokens.Values[saved.Find("work").Id]);
        Assert.False(saved.Find("local").HasAccessToken);
    }

    private ConnectionSettingsViewModel CreateModel()
    {
        var connector = new Connector(Store);
        return new(Manager, connector, new ClientStartup(Store, connector), new Browser());
    }

    private async Task ChangeDestinationAsync(string change, ServerConnection original)
    {
        if (change == "recreate")
        {
            await Manager.RemoveAsync(original.Name);
            await Manager.AddAsync(original.Name, original.Address);
        }
        else if (change == "same-id-address")
        {
            await Store.UpdateAsync(config => Task.FromResult(config with
            {
                Servers = config.Servers.Select(server => server.Id == original.Id
                    ? server with { Address = "https://changed.example" } : server).ToList()
            }));
        }
        else
        {
            await Manager.UpdateAddressAsync(original.Name, "https://changed.example");
        }
    }

    public void Dispose() => File.Delete(_database);

    private sealed class PausedInput : TextReader
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<string?> Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override string? ReadLine()
        {
            Started.TrySetResult();
            return Resume.Task.GetAwaiter().GetResult();
        }
        public override ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            return new(Resume.Task.WaitAsync(cancellationToken));
        }
    }

    private sealed class Tokens : IServerTokenStore
    {
        public Dictionary<Guid, string> Values { get; } = [];
        public Task<string?> GetAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult(Values.GetValueOrDefault(id));
        public Task SetAsync(Guid id, string token, CancellationToken cancellationToken = default)
        {
            Values[id] = token;
            return Task.CompletedTask;
        }
        public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
        {
            Values.Remove(id);
            return Task.CompletedTask;
        }
    }

    private sealed class Connector(IClientConfigurationStore store) : IServerConnector
    {
        public async Task<(ServerConnection Server, IVibeReClient Client)> ConnectAsync(string? name = null,
            string? tokenOverride = null, CancellationToken cancellationToken = default) =>
            ((await store.LoadAsync(cancellationToken)).Find(name), new Client());
    }

    private sealed class Client : IVibeReClient
    {
        public Task<AuthStatus> GetStatusAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new AuthStatus { State = AuthState.SignedOut });
        public Task<AuthStatus> LoginAsync(string apiKey, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AuthStatus> LogoutAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<BrowserLoginResult> LoginWithBrowserAsync(Func<BrowserLoginResult, Task> showBrowser,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class Browser : IBrowserLauncher
    {
        public Task OpenAsync(Uri uri) => throw new NotSupportedException();
    }
}
