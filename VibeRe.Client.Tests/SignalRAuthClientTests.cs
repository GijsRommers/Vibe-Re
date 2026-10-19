using System.Net;
using System.Threading.Channels;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.SignalR.Protocol;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using VibeRe.Client.Application;
using VibeRe.Client.SignalR;
using Protocol = VibeRe.Contracts;

namespace VibeRe.Client.Tests;

public sealed class SignalRAuthClientTests
{
    private const string InactiveStream = "The 'StreamAsChannelCoreAsync' method cannot be called if the connection is not active";

    [Fact]
    public async Task InactiveStreamRaceRetriesTheSameAttemptEvenAfterStateIsConnected()
    {
        using var server = new SignalRTestServer();
        var connection = (FaultingStreamConnection)server.CreateConnection(services => new FaultingStreamConnection(services,
            new InvalidOperationException(InactiveStream), new InvalidOperationException(InactiveStream)));
        await using var client = new SignalRAuthClient(connection);
        var opened = 0;

        var result = await client.LoginWithBrowserAsync(_ => { opened++; return Task.CompletedTask; })
            .WaitAsync(TimeSpan.FromSeconds(15));

        Assert.Equal(BrowserLoginState.Completed, result.State);
        Assert.Equal(1, opened);
        Assert.Equal(3, connection.Attempts.Count);
        Assert.All(connection.Attempts, attempt => Assert.Equal(("attempt", HubConnectionState.Connected), attempt));
        Assert.Equal(1, server.Scenario.Calls.Count(call => call == "StartBrowserLogin"));
        Assert.Equal("attempt", Assert.Single(server.Scenario.WatchedAttempts));
        Assert.Empty(server.Scenario.CanceledAttempts);
    }

    [Theory]
    [InlineData("hub")]
    [InlineData("unauthorized")]
    [InlineData("forbidden")]
    [InlineData("invalid-operation")]
    [InlineData("disposed")]
    public async Task ObservationNeverRetriesServerAuthorizationOrUnrelatedFailures(string kind)
    {
        Exception failure = kind switch
        {
            "hub" => new HubException("Access was revoked."),
            "unauthorized" => new HttpRequestException("Unauthorized", null, HttpStatusCode.Unauthorized),
            "forbidden" => new HttpRequestException("Forbidden", null, HttpStatusCode.Forbidden),
            "invalid-operation" => new InvalidOperationException("Invalid serializer configuration."),
            _ => new ObjectDisposedException("connection")
        };
        using var server = new SignalRTestServer();
        var connection = (FaultingStreamConnection)server.CreateConnection(services => new FaultingStreamConnection(services, failure));
        await using var client = new SignalRAuthClient(connection);

        var error = await Assert.ThrowsAsync<ServerConnectionException>(() =>
            client.LoginWithBrowserAsync(_ => Task.CompletedTask).WaitAsync(TimeSpan.FromSeconds(15)));

        Assert.Same(failure, error.InnerException);
        Assert.Single(connection.Attempts);
        Assert.Equal(1, server.Scenario.Calls.Count(call => call == "StartBrowserLogin"));
        Assert.Empty(server.Scenario.WatchedAttempts);
    }

    [Fact]
    public async Task CallerCancellationInterruptsInactiveStreamRecovery()
    {
        using var server = new SignalRTestServer();
        var connection = (FaultingStreamConnection)server.CreateConnection(services => new FaultingStreamConnection(services,
            new InvalidOperationException(InactiveStream)));
        await using var client = new SignalRAuthClient(connection);
        using var cancellation = new CancellationTokenSource();
        var login = client.LoginWithBrowserAsync(_ => Task.CompletedTask, cancellation.Token);
        await connection.FailedAttempt.Task.WaitAsync(TimeSpan.FromSeconds(15));

        cancellation.Cancel();

        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => login.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Equal(1, server.Scenario.Calls.Count(call => call == "StartBrowserLogin"));
    }

    [Fact]
    public async Task SuppliedConnectionUsesSharedProtocolAndIsDisposedAsynchronously()
    {
        using var server = new SignalRTestServer();
        var connection = server.CreateConnection();
        await using (IVibeReClient client = new SignalRAuthClient(connection))
        {
            Assert.Equal(AuthState.Authenticated, (await client.LoginAsync("api-key")).State);
            Assert.Equal(AuthState.Authenticated, (await client.GetStatusAsync()).State);
            Assert.Equal(AuthState.Authenticated, (await client.LogoutAsync()).State);
            var login = await client.LoginWithBrowserAsync(_ => Task.CompletedTask);
            Assert.Equal(BrowserLoginState.Completed, login.State);
            Assert.Equal(HubConnectionState.Connected, connection.State);
        }

        Assert.Equal(HubConnectionState.Disconnected, connection.State);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => connection.StartAsync());
        Assert.Equal(new[] { "Login", "GetStatus", "Logout", "StartBrowserLogin", "WatchBrowserLogin" },
            server.Scenario.Calls);
    }

    [Fact]
    public void SharedAdapterOnlyAdvertisesAsynchronousDisposal()
    {
        Assert.True(typeof(IAsyncDisposable).IsAssignableFrom(typeof(SignalRAuthClient)));
        Assert.False(typeof(IDisposable).IsAssignableFrom(typeof(SignalRAuthClient)));
    }

    [Theory]
    [InlineData("https://identity.provider.example/verify")]
    [InlineData("https://identity.provider.example:8443/verify")]
    public async Task BrowserLoginUsesProviderUrlAndMessagesFromServer(string signInUrl)
    {
        using var server = new SignalRTestServer(new()
        {
            Start = _ => Task.FromResult(TestAuthScenario.StartedAttempt() with
            {
                SignInUrl = signInUrl,
                Message = "Continue with Example AI."
            }),
            Watch = (_, ct) => TestAuthScenario.Replies([
                new()
                {
                    State = Protocol.BrowserLoginState.Failed,
                    Message = "Example AI requires an active subscription.",
                    AuthStatus = new()
                    {
                        State = Protocol.AuthState.SubscriptionRequired,
                        Message = "Choose an Example AI plan."
                    }
                }], ct)
        });
        await using IVibeReClient client = new SignalRAuthClient(server.CreateConnection());
        BrowserLoginResult? shown = null;

        var result = await client.LoginWithBrowserAsync(attempt =>
        {
            shown = attempt;
            return Task.CompletedTask;
        });

        Assert.NotNull(shown);
        Assert.Equal(signInUrl, shown.SignInUrl);
        Assert.Equal("Continue with Example AI.", shown.Message);
        Assert.Equal(BrowserLoginState.Failed, result.State);
        Assert.Equal("Example AI requires an active subscription.", result.Message);
        Assert.Equal(new AuthStatus
        {
            State = AuthState.SubscriptionRequired,
            Message = "Choose an Example AI plan."
        }, result.AuthStatus);
        Assert.Equal("attempt", Assert.Single(server.Scenario.WatchedAttempts));
        Assert.Empty(server.Scenario.CanceledAttempts);
    }

    [Fact]
    public void SharedAdapterHasNoNativeInfrastructureDependencies()
    {
        var dependencies = typeof(SignalRAuthClient).Assembly.GetReferencedAssemblies();

        Assert.Contains(dependencies, dependency => dependency.Name == "VibeRe.Client.Application");
        Assert.Contains(dependencies, dependency => dependency.Name == "VibeRe.Contracts");
        Assert.DoesNotContain(dependencies, dependency => dependency.Name is
            "VibeRe.Client.Infrastructure" or "Microsoft.Data.Sqlite" or "Latchkey");
    }

    // Keep a real, connected SignalR transport while deterministically injecting the exception
    // that raced its automatic reconnection on Windows. Public State is already Connected.
    private sealed class FaultingStreamConnection(IServiceProvider services, params Exception[] failures)
        : HubConnection(services.GetRequiredService<IConnectionFactory>(), services.GetRequiredService<IHubProtocol>(),
            services.GetRequiredService<EndPoint>(), services, services.GetRequiredService<ILoggerFactory>())
    {
        private readonly Queue<Exception> _failures = new(failures);
        public List<(string Attempt, HubConnectionState State)> Attempts { get; } = [];
        public TaskCompletionSource FailedAttempt { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override Task<ChannelReader<object?>> StreamAsChannelCoreAsync(string methodName, Type returnType,
            object?[] args, CancellationToken cancellationToken = default)
        {
            Attempts.Add(((string)args[0]!, State));
            if (_failures.TryDequeue(out var error))
            {
                FailedAttempt.TrySetResult();
                return Task.FromException<ChannelReader<object?>>(error);
            }
            return base.StreamAsChannelCoreAsync(methodName, returnType, args, cancellationToken);
        }
    }
}
