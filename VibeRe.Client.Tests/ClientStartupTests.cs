using VibeRe.Client.Application;
using VibeRe.Client.Application.Configuration;

namespace VibeRe.Client.Tests;

public sealed class ClientStartupTests
{
    [Fact]
    public void SharedOwnershipContractsOnlyRequireAsynchronousDisposal()
    {
        Assert.True(typeof(IAsyncDisposable).IsAssignableFrom(typeof(IVibeReClient)));
        Assert.True(typeof(IAsyncDisposable).IsAssignableFrom(typeof(ServerSession)));
        Assert.False(typeof(IDisposable).IsAssignableFrom(typeof(IVibeReClient)));
        Assert.False(typeof(IDisposable).IsAssignableFrom(typeof(ServerSession)));
    }

    [Fact]
    public async Task RequiredStartupConnectsBeforeCheckingAuthentication()
    {
        var calls = new List<string>();
        var configuration = new Configuration(ClientConfiguration.Default(), calls);
        var client = new Client(calls);
        var connector = new Connector(client, calls);

        var result = await new ClientStartup(configuration, connector).StartAsync("work", "access-token");
        await using var session = Assert.IsType<ServerSession>(result);

        Assert.NotNull(session);
        Assert.Equal(new[] { "configuration", "connect", "status" }, calls);
        Assert.Equal("work", connector.Name);
        Assert.Equal("access-token", connector.Token);
        Assert.Same(client, session.Client);
        Assert.Equal(connector.Connection.Server, session.Server);
        Assert.Equal(AuthState.SignedOut, session.Status.State);
        Assert.False(client.Disposed);
        await session.DisposeAsync();
        Assert.True(client.Disposed);
    }

    [Fact]
    public async Task OptionalStartupDoesNotConnectOrCheckAuthenticationEvenWithoutProfiles()
    {
        var calls = new List<string>();
        var configuration = new Configuration(new(null, [], ServerRequired: false), calls);
        var client = new Client(calls);
        var connector = new Connector(client, calls);

        var result = await new ClientStartup(configuration, connector).StartAsync();

        Assert.IsType<ServerOptional>(result);
        Assert.Equal(new[] { "configuration" }, calls);
        Assert.False(client.Disposed);
    }

    [Fact]
    public async Task StatusFailureDisposesClientAndPreservesError()
    {
        var calls = new List<string>();
        var error = new InvalidOperationException("Server unavailable.");
        var client = new Client(calls, _ => Task.FromException<AuthStatus>(error));
        var startup = new ClientStartup(new Configuration(ClientConfiguration.Default(), calls), new Connector(client, calls));

        Assert.Same(error, await Assert.ThrowsAsync<InvalidOperationException>(() => startup.StartAsync()));
        Assert.True(client.Disposed);
    }

    [Fact]
    public async Task StartupForwardsCancellationToConfigurationConnectionAndStatus()
    {
        using var cancellation = new CancellationTokenSource();
        var calls = new List<string>();
        var configuration = new Configuration(ClientConfiguration.Default(), calls);
        var client = new Client(calls);
        var connector = new Connector(client, calls);

        var result = await new ClientStartup(configuration, connector).StartAsync(cancellationToken: cancellation.Token);
        await using var session = Assert.IsType<ServerSession>(result);

        Assert.Equal(cancellation.Token, configuration.CancellationToken);
        Assert.Equal(cancellation.Token, connector.CancellationToken);
        Assert.Equal(cancellation.Token, client.CancellationToken);
    }

    [Fact]
    public async Task StatusCancellationDisposesClientAndRemainsCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var calls = new List<string>();
        var client = new Client(calls, ct =>
        {
            cancellation.Cancel();
            return Task.FromCanceled<AuthStatus>(ct);
        });
        var startup = new ClientStartup(new Configuration(ClientConfiguration.Default(), calls), new Connector(client, calls));

        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => startup.StartAsync(cancellationToken: cancellation.Token));
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.True(client.Disposed);
    }

    [Fact]
    public async Task SessionDisposesItsClientThroughTheSharedContract()
    {
        var client = new Client([]);

        await using (var session = new ServerSession(ClientConfiguration.Default().Find(), client, new AuthStatus { State = AuthState.SignedOut }))
            Assert.False(client.Disposed);

        Assert.True(client.Disposed);
    }

    [Fact]
    public async Task SessionAwaitsAsynchronousClientDisposal()
    {
        var client = new AsyncClient();

        await using (var session = new ServerSession(ClientConfiguration.Default().Find(), client, new AuthStatus { State = AuthState.SignedOut }))
            Assert.False(client.Disposed);

        Assert.True(client.Disposed);
    }

    [Fact]
    public async Task StatusFailureAwaitsAsynchronousDisposalAndPreservesError()
    {
        var calls = new List<string>();
        var error = new InvalidOperationException("Server unavailable.");
        var client = new AsyncClient(error);
        var startup = new ClientStartup(new Configuration(ClientConfiguration.Default(), calls), new Connector(client, calls));

        Assert.Same(error, await Assert.ThrowsAsync<InvalidOperationException>(() => startup.StartAsync()));
        Assert.True(client.Disposed);
    }

    [Theory]
    [InlineData(AuthState.SignedOut)]
    [InlineData(AuthState.InvalidCredential)]
    [InlineData(AuthState.Authenticated)]
    [InlineData(AuthState.SubscriptionRequired)]
    [InlineData(AuthState.Unavailable)]
    public async Task SessionContainsTheActualStatusFromTheSelectedServer(AuthState state)
    {
        var calls = new List<string>();
        var status = new AuthStatus { State = state, Message = "Actual server status." };
        var client = new Client(calls, _ => Task.FromResult(status));
        var startup = new ClientStartup(new Configuration(ClientConfiguration.Default(), calls), new Connector(client, calls));

        var result = await startup.StartAsync();
        await using var session = Assert.IsType<ServerSession>(result);

        Assert.Same(status, session.Status);
    }

    [Fact]
    public async Task LateStatusAfterCancellationIsDisposedInsteadOfBecomingReady()
    {
        using var cancellation = new CancellationTokenSource();
        var calls = new List<string>();
        var client = new Client(calls, _ =>
        {
            cancellation.Cancel();
            return Task.FromResult(new AuthStatus { State = AuthState.Authenticated });
        });
        var startup = new ClientStartup(new Configuration(ClientConfiguration.Default(), calls), new Connector(client, calls));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => startup.StartAsync(cancellationToken: cancellation.Token));

        Assert.True(client.Disposed);
    }

    [Fact]
    public async Task LateConnectionAfterCancellationIsDisposedWithoutCheckingStatus()
    {
        using var cancellation = new CancellationTokenSource();
        var calls = new List<string>();
        var client = new Client(calls);
        var connector = new Connector(client, calls) { OnConnect = cancellation.Cancel };
        var startup = new ClientStartup(new Configuration(ClientConfiguration.Default(), calls), connector);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => startup.StartAsync(cancellationToken: cancellation.Token));

        Assert.Equal(new[] { "configuration", "connect" }, calls);
        Assert.True(client.Disposed);
    }

    [Fact]
    public async Task CanceledOptionalStartupDoesNotReportSuccess()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var calls = new List<string>();
        var client = new Client(calls);
        var startup = new ClientStartup(new Configuration(new(null, [], ServerRequired: false), calls), new Connector(client, calls));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => startup.StartAsync(cancellationToken: cancellation.Token));

        Assert.Equal(new[] { "configuration" }, calls);
        Assert.False(client.Disposed);
    }

    private sealed class Configuration(ClientConfiguration config, List<string> calls) : IClientConfigurationStore
    {
        public CancellationToken CancellationToken { get; private set; }
        public Task<ClientConfiguration> LoadAsync(CancellationToken cancellationToken = default)
        {
            calls.Add("configuration");
            CancellationToken = cancellationToken;
            return Task.FromResult(config);
        }
        public Task<ClientConfiguration> UpdateAsync(Func<ClientConfiguration, Task<ClientConfiguration>> update,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class Connector(IVibeReClient client, List<string> calls) : IServerConnector
    {
        public (ServerConnection Server, IVibeReClient Client) Connection { get; } = (ClientConfiguration.Default().Find(), client);
        public Action? OnConnect { get; init; }
        public string? Name { get; private set; }
        public string? Token { get; private set; }
        public CancellationToken CancellationToken { get; private set; }
        public Task<(ServerConnection Server, IVibeReClient Client)> ConnectAsync(string? name = null, string? tokenOverride = null,
            CancellationToken cancellationToken = default)
        {
            calls.Add("connect");
            Name = name;
            Token = tokenOverride;
            CancellationToken = cancellationToken;
            OnConnect?.Invoke();
            return Task.FromResult(Connection);
        }
    }

    private sealed class Client(List<string> calls, Func<CancellationToken, Task<AuthStatus>>? getStatus = null) : IVibeReClient
    {
        public bool Disposed { get; private set; }
        public CancellationToken CancellationToken { get; private set; }
        public Task<AuthStatus> GetStatusAsync(CancellationToken cancellationToken = default)
        {
            calls.Add("status");
            CancellationToken = cancellationToken;
            return getStatus?.Invoke(cancellationToken) ?? Task.FromResult(new AuthStatus { State = AuthState.SignedOut });
        }
        public Task<AuthStatus> LoginAsync(string apiKey, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AuthStatus> LogoutAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<BrowserLoginResult> LoginWithBrowserAsync(Func<BrowserLoginResult, Task> showBrowser,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class AsyncClient(Exception? statusError = null) : IVibeReClient
    {
        public bool Disposed { get; private set; }
        public Task<AuthStatus> GetStatusAsync(CancellationToken cancellationToken = default) => statusError is null
            ? Task.FromResult(new AuthStatus { State = AuthState.SignedOut })
            : Task.FromException<AuthStatus>(statusError);
        public Task<AuthStatus> LoginAsync(string apiKey, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AuthStatus> LogoutAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<BrowserLoginResult> LoginWithBrowserAsync(Func<BrowserLoginResult, Task> showBrowser,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public async ValueTask DisposeAsync()
        {
            await Task.Yield();
            Disposed = true;
        }
    }
}
