using Microsoft.AspNetCore.SignalR;
using VibeRe.Client.Application;
using Protocol = VibeRe.Contracts;

namespace VibeRe.Client.Tests;

public sealed class ClientTransportTests
{
    [Fact]
    public async Task NativeClientDisposesThroughTheSharedInterface()
    {
        using var server = new SignalRTestServer();
        IVibeReClient client = server.CreateClient();
        try
        {
            Assert.Equal(AuthState.Authenticated, (await client.GetStatusAsync()).State);
        }
        finally
        {
            await client.DisposeAsync();
        }

        var error = await Assert.ThrowsAsync<ServerConnectionException>(() => client.GetStatusAsync());
        Assert.IsType<ObjectDisposedException>(error.InnerException);
    }

    [Theory]
    [InlineData(Protocol.AuthState.SignedOut, AuthState.SignedOut, true)]
    [InlineData(Protocol.AuthState.Authenticated, AuthState.Authenticated, false)]
    [InlineData(Protocol.AuthState.InvalidCredential, AuthState.InvalidCredential, true)]
    [InlineData(Protocol.AuthState.SubscriptionRequired, AuthState.SubscriptionRequired, false)]
    [InlineData(Protocol.AuthState.Unavailable, AuthState.Unavailable, false)]
    public async Task AuthenticationRepliesBecomeApplicationStatus(Protocol.AuthState wireState, AuthState state, bool needsLogin)
    {
        using var server = new SignalRTestServer(new()
        {
            Authenticate = (_, _) => Task.FromResult(new Protocol.AuthStatusReply
            {
                State = wireState, Message = "Provider status."
            })
        });
        await using IVibeReClient client = server.CreateClient();

        foreach (var result in new[] { await client.LoginAsync("api-key"), await client.GetStatusAsync(), await client.LogoutAsync() })
        {
            Assert.Equal(state, result.State);
            Assert.Equal("Provider status.", result.Message);
            Assert.Equal(needsLogin, result.NeedsLogin);
        }
        Assert.Equal(new[] { "Login", "GetStatus", "Logout" }, server.Scenario.Calls);
        Assert.Equal("api-key", Assert.Single(server.Scenario.ApiKeys));
    }

    [Theory]
    [InlineData(Protocol.BrowserLoginState.Completed, BrowserLoginState.Completed)]
    [InlineData(Protocol.BrowserLoginState.Failed, BrowserLoginState.Failed)]
    [InlineData(Protocol.BrowserLoginState.Expired, BrowserLoginState.Expired)]
    [InlineData(Protocol.BrowserLoginState.Canceled, BrowserLoginState.Canceled)]
    public async Task StreamedBrowserRepliesBecomeApplicationResults(Protocol.BrowserLoginState wireState, BrowserLoginState state)
    {
        using var server = new SignalRTestServer(new()
        {
            Watch = (_, ct) => TestAuthScenario.Replies([
                TestAuthScenario.StartedAttempt(),
                new()
                {
                    State = wireState, Message = "Browser result.",
                    AuthStatus = new() { State = Protocol.AuthState.Authenticated, Message = "Verified." }
                }], ct)
        });
        await using IVibeReClient client = server.CreateClient();
        BrowserLoginResult? shown = null;

        var result = await client.LoginWithBrowserAsync(attempt =>
        {
            shown = attempt;
            return Task.CompletedTask;
        });

        Assert.NotNull(shown);
        Assert.Equal(BrowserLoginState.Waiting, shown.State);
        Assert.Equal("https://provider.example/login", shown.SignInUrl);
        Assert.Equal("Open the browser.", shown.Message);
        Assert.Null(shown.AuthStatus);
        Assert.Equal(state, result.State);
        Assert.Equal("Browser result.", result.Message);
        Assert.Equal(new AuthStatus { State = AuthState.Authenticated, Message = "Verified." }, result.AuthStatus);
        Assert.Equal(new[] { "StartBrowserLogin", "WatchBrowserLogin" }, server.Scenario.Calls);
        Assert.Equal("attempt", Assert.Single(server.Scenario.WatchedAttempts));
        Assert.Empty(server.Scenario.CanceledAttempts);
    }

    public static IEnumerable<object[]> HubFailures() =>
        from method in new[] { "Login", "GetStatus", "Logout", "StartBrowserLogin", "WatchBrowserLogin" }
        from failure in new[] { "fault", "server-canceled", "hub" }
        select new object[] { method, failure };

    [Theory]
    [MemberData(nameof(HubFailures))]
    public async Task HubFailuresBecomeApplicationErrorsWithoutCancelingCaller(string failingMethod, string failure)
    {
        const string privateDetail = "Provider secret diagnostic: api-key-do-not-expose.";
        const string safeDetail = "The server request could not be completed.";
        var error = failure switch
        {
            "server-canceled" => (Exception)new OperationCanceledException(privateDetail),
            "hub" => new HubException(safeDetail),
            _ => new InvalidOperationException(privateDetail)
        };
        using var server = new SignalRTestServer(new()
        {
            Authenticate = (method, _) => method == failingMethod
                ? Task.FromException<Protocol.AuthStatusReply>(error)
                : Task.FromResult(new Protocol.AuthStatusReply()),
            Start = _ => failingMethod == "StartBrowserLogin"
                ? Task.FromException<Protocol.BrowserLoginReply>(error)
                : Task.FromResult(TestAuthScenario.StartedAttempt()),
            Watch = (_, ct) => TestAuthScenario.Failure(error, ct)
        });
        await using IVibeReClient client = server.CreateClient();
        using var caller = new CancellationTokenSource();

        var result = await Assert.ThrowsAsync<ServerConnectionException>(() =>
            InvokeAsync(client, failingMethod, caller.Token).WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.False(caller.IsCancellationRequested);
        Assert.IsType<HubException>(result.InnerException);
        Assert.False(string.IsNullOrWhiteSpace(result.Message));
        Assert.DoesNotContain(privateDetail, result.Message);
        if (failure == "hub") Assert.Contains(safeDetail, result.Message);
        Assert.Equal(1, server.Scenario.Calls.Count(method => method == failingMethod));
        if (failingMethod == "WatchBrowserLogin")
            Assert.Equal(1, server.Scenario.Calls.Count(method => method == "StartBrowserLogin"));
    }

    [Theory]
    [InlineData("Login")]
    [InlineData("GetStatus")]
    [InlineData("Logout")]
    [InlineData("StartBrowserLogin")]
    [InlineData("WatchBrowserLogin")]
    public async Task CallerCancellationPreservesTheCallerToken(string method)
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var server = new SignalRTestServer(new()
        {
            Authenticate = async (_, ct) =>
            {
                started.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct);
                return new();
            },
            Start = async ct =>
            {
                if (method != "StartBrowserLogin") return TestAuthScenario.StartedAttempt();
                started.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct);
                return new();
            },
            Watch = (_, ct) => TestAuthScenario.WaitForCancellation(started, ct)
        });
        await using IVibeReClient client = server.CreateClient();
        using var caller = new CancellationTokenSource();
        var operation = InvokeAsync(client, method, caller.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        caller.Cancel();

        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(caller.Token, error.CancellationToken);
        Assert.Equal(1, server.Scenario.Calls.Count(call => call == method));
        Assert.Equal(method == "WatchBrowserLogin" ? 1 : 0, server.Scenario.CanceledAttempts.Count);
    }

    [Theory]
    [InlineData("")]
    [InlineData("http://provider.example/login")]
    [InlineData("ftp://provider.example/login")]
    [InlineData("javascript:alert(1)")]
    [InlineData("https://user@provider.example/login")]
    [InlineData("https://user:password@provider.example/login")]
    [InlineData("https://provider.example\\login")]
    [InlineData("https://provider.example/login with spaces")]
    [InlineData("https:///login")]
    [InlineData("//provider.example/login")]
    [InlineData("/login")]
    public async Task UntrustedBrowserUrlIsRejectedAndAttemptIsCanceled(string url)
    {
        using var server = new SignalRTestServer(new()
        {
            Start = _ => Task.FromResult(TestAuthScenario.StartedAttempt() with { SignInUrl = url })
        });
        await using IVibeReClient client = server.CreateClient();
        var opened = false;

        var error = await Assert.ThrowsAsync<ServerConnectionException>(() => client.LoginWithBrowserAsync(_ =>
        {
            opened = true;
            return Task.CompletedTask;
        }));

        Assert.Contains("untrusted", error.Message);
        Assert.False(opened);
        Assert.Equal(new[] { "StartBrowserLogin", "CancelBrowserLogin" }, server.Scenario.Calls);
        Assert.Equal("attempt", Assert.Single(server.Scenario.CanceledAttempts));
    }

    [Fact]
    public async Task UnknownAuthenticationStatusIsRejected()
    {
        using var server = new SignalRTestServer(new()
        {
            Authenticate = (_, _) => Task.FromResult(new Protocol.AuthStatusReply { State = (Protocol.AuthState)999 })
        });
        await using IVibeReClient client = server.CreateClient();

        var error = await Assert.ThrowsAsync<ServerConnectionException>(() => client.GetStatusAsync());

        Assert.Contains("unsupported authentication status", error.Message);
    }

    [Fact]
    public async Task UnknownBrowserStatusIsRejectedAndCleanedUp()
    {
        using var server = new SignalRTestServer(new()
        {
            Watch = (_, ct) => TestAuthScenario.Replies([
                TestAuthScenario.StartedAttempt() with { State = (Protocol.BrowserLoginState)999 }], ct)
        });
        await using IVibeReClient client = server.CreateClient();

        var error = await Assert.ThrowsAsync<ServerConnectionException>(() => client.LoginWithBrowserAsync(_ => Task.CompletedTask));

        Assert.Contains("unsupported browser login status", error.Message);
        Assert.Equal("attempt", Assert.Single(server.Scenario.CanceledAttempts));
    }

    private static Task InvokeAsync(IVibeReClient client, string method, CancellationToken cancellationToken) => method switch
    {
        "Login" => client.LoginAsync("api-key", cancellationToken),
        "GetStatus" => client.GetStatusAsync(cancellationToken),
        "Logout" => client.LogoutAsync(cancellationToken),
        "StartBrowserLogin" or "WatchBrowserLogin" => client.LoginWithBrowserAsync(_ => Task.CompletedTask, cancellationToken),
        _ => throw new ArgumentOutOfRangeException(nameof(method))
    };
}
