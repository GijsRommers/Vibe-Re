using VibeRe.Client.Application;
using VibeRe.Client.Application.Configuration;

namespace VibeRe.Client.Tests;

public sealed class ClientStartupTests
{
    [Fact]
    public async Task RequiredStartupConnectsBeforeCheckingAuthentication()
    {
        var calls = new List<string>();
        var configuration = new Configuration(ClientConfiguration.Default(), calls);
        var client = new Client(calls);
        var connector = new Connector(client, calls);

        using var session = await new ClientStartup(configuration, connector).StartAsync("work", "access-token");

        Assert.NotNull(session);
        Assert.Equal(new[] { "configuration", "connect", "status" }, calls);
        Assert.Equal("work", connector.Name);
        Assert.Equal("access-token", connector.Token);
        Assert.Same(client, session.Client);
        Assert.Equal(connector.Session.Server, session.Server);
        Assert.NotNull(session.Status);
        Assert.Equal(AuthState.SignedOut, session.Status!.State);
        Assert.False(client.Disposed);
        session.Dispose();
        Assert.True(client.Disposed);
    }

    [Fact]
    public async Task OptionalStartupDoesNotConnectOrCheckAuthenticationEvenWithoutProfiles()
    {
        var calls = new List<string>();
        var configuration = new Configuration(new(null, [], ServerRequired: false), calls);
        var client = new Client(calls);
        var connector = new Connector(client, calls);

        var session = await new ClientStartup(configuration, connector).StartAsync();

        Assert.Null(session);
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

        using var session = await new ClientStartup(configuration, connector).StartAsync(cancellationToken: cancellation.Token);

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
        public ServerSession Session { get; } = new(ClientConfiguration.Default().Find(), client);
        public string? Name { get; private set; }
        public string? Token { get; private set; }
        public CancellationToken CancellationToken { get; private set; }
        public Task<ServerSession> ConnectAsync(string? name = null, string? tokenOverride = null,
            CancellationToken cancellationToken = default)
        {
            calls.Add("connect");
            Name = name;
            Token = tokenOverride;
            CancellationToken = cancellationToken;
            return Task.FromResult(Session);
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
        public void Dispose() => Disposed = true;
    }
}
