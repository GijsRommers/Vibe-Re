using Microsoft.AspNetCore.SignalR.Client;
using VibeRe.Client.Application;
using VibeRe.Client.SignalR;
using Protocol = VibeRe.Contracts;

namespace VibeRe.Client.Tests;

public sealed class SignalRAuthClientTests
{
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
}
