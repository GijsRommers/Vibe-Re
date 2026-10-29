using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using VibeRe.Contracts;
using VibeRe.Server.Application.Providers.Auth;
using WireBrowserState = VibeRe.Contracts.BrowserLoginState;

namespace VibeRe.Server.Tests;

public class ProviderAuthIntegrationTests
{
    [Fact]
    public async Task ReadinessIdentifiesSignalRRuntimeWithoutRequiringServerToken()
    {
        using var server = new SignalRTestServer();
        using var http = server.CreateClient();
        var response = await http.GetAsync(ServerProtocol.ReadinessPath);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);
        Assert.Equal("vibe-re", document.RootElement.GetProperty("application").GetString());
        Assert.Equal(ServerProtocol.Version, document.RootElement.GetProperty("protocolVersion").GetInt32());
        Assert.Equal(ServerProtocol.Transport, document.RootElement.GetProperty("transport").GetString());
        Assert.DoesNotContain("token", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ClientsSeeSameServerLoginAndOtherServersRemainSignedOut()
    {
        using var first = new SignalRTestServer();
        using var second = new SignalRTestServer();
        await using var cli = first.Connect();
        await using var desktop = first.Connect();
        await using var otherServer = second.Connect();
        Assert.Equal(AuthState.SignedOut, (await cli.GetStatusAsync()).State);
        Assert.Equal(AuthState.Authenticated, (await cli.LoginAsync("valid-key")).State);
        Assert.Equal(AuthState.Authenticated, (await desktop.GetStatusAsync()).State);
        Assert.Equal(AuthState.SignedOut, (await otherServer.GetStatusAsync()).State);
        Assert.Equal(AuthState.InvalidCredential, (await desktop.LoginAsync("bad-key")).State);
        Assert.Equal(AuthState.Authenticated, (await cli.GetStatusAsync()).State);
        await desktop.LogoutAsync();
        Assert.Equal(AuthState.SignedOut, (await cli.GetStatusAsync()).State);
        Assert.Empty(first.Secrets.Values);
        first.AssertResponsesExclude("valid-key", "bad-key", "server-token");
    }

    [Fact]
    public async Task BrowserAndApiKeyLoginPersistOnServerWithoutReturningSecrets()
    {
        using var server = new SignalRTestServer();
        await using var client = server.Connect();
        string? opened = null;
        var completion = await client.LoginWithBrowserAsync(attempt =>
        {
            opened = attempt.SignInUrl;
            Assert.DoesNotContain("verifier", attempt.ToString(), StringComparison.OrdinalIgnoreCase);
            return Task.CompletedTask;
        });
        Assert.Equal("https://console.mistral.ai/login", opened);
        Assert.Equal(BrowserLoginState.Completed, completion.State);
        Assert.Equal(AuthState.Authenticated, completion.AuthStatus!.State);
        Assert.DoesNotContain("browser-key", completion.ToString());
        Assert.NotNull(server.Browser.LastVerifier);
        Assert.Contains("browser-key", server.Secrets.Values.Values);
        await client.LoginAsync("new-api-key");
        Assert.Contains("new-api-key", server.Secrets.Values.Values);
        Assert.DoesNotContain("browser-key", server.Secrets.Values.Values);
        Assert.Equal(AuthState.Authenticated, (await client.GetStatusAsync()).State);
        server.AssertResponsesExclude("browser-key", "new-api-key", "exchange-secret", "server-token", server.Browser.LastVerifier);
    }

    [Fact]
    public async Task LogoutCancelsPendingBrowserLogin()
    {
        using var server = new SignalRTestServer();
        await using var client = server.Connect();
        var result = await client.LoginWithBrowserAsync(async _ => { await client.LogoutAsync(); });
        Assert.Equal(BrowserLoginState.Canceled, result.State);
        Assert.Empty(server.Secrets.Values);
        Assert.Equal(AuthState.SignedOut, (await client.GetStatusAsync()).State);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("wrong-token")]
    public async Task MissingOrWrongServerTokenCannotReadOrChangeProviderCredentials(string? token)
    {
        using var server = new SignalRTestServer();
        await using var client = server.Connect(token);
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => client.LoginAsync("valid-key"));
        Assert.Equal(HttpStatusCode.Unauthorized, error.StatusCode);
        Assert.Empty(server.Secrets.Values);
        server.AssertResponsesExclude("valid-key", "server-token");
    }

    [Fact]
    public async Task InvalidRequestAndStorageFailuresAreSafeHubErrors()
    {
        using var server = new SignalRTestServer();
        await using var client = server.Connect();
        await Assert.ThrowsAsync<HubException>(() => client.LoginAsync(""));
        server.Secrets.Fail = true;
        var failed = await Assert.ThrowsAsync<HubException>(() => client.LoginAsync("valid-key"));
        Assert.DoesNotContain("sensitive-storage-value", failed.ToString());
        server.AssertResponsesExclude("sensitive-storage-value", "valid-key");
    }

    [Fact]
    public async Task ProviderMetadataAndSecretSurviveServerRestart()
    {
        var directory = Path.Combine(Path.GetTempPath(), "vibere-restart-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        var database = Path.Combine(directory, "server.db");
        var secrets = new TestSecrets();
        try
        {
            using (var first = new SignalRTestServer(database, secrets))
            await using (var client = first.Connect()) await client.LoginAsync("persistent-key");
            using var restarted = new SignalRTestServer(database, secrets);
            await using var reconnect = restarted.Connect();
            Assert.Equal(AuthState.Authenticated, (await reconnect.GetStatusAsync()).State);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData(BrowserPollState.Completed, WireBrowserState.Completed)]
    [InlineData(BrowserPollState.Denied, WireBrowserState.Failed)]
    [InlineData(BrowserPollState.Expired, WireBrowserState.Expired)]
    [InlineData((BrowserPollState)999, WireBrowserState.Failed)]
    public async Task BrowserStreamCompletesWithTerminalProviderResult(BrowserPollState providerState, WireBrowserState expected)
    {
        using var server = new SignalRTestServer();
        server.Browser.PollState = providerState;
        await using var hub = server.CreateHub();
        await hub.StartAsync();
        var attempt = await hub.InvokeAsync<BrowserLoginReply>("StartBrowserLogin");
        var updates = await WatchToEnd(hub, attempt.AttemptId);
        Assert.Equal(expected, updates[^1].State);
        Assert.All(updates, update => Assert.Equal(attempt.AttemptId, update.AttemptId));
        Assert.Equal(expected == WireBrowserState.Completed, !server.Secrets.Values.IsEmpty);
        server.AssertResponsesExclude("browser-key", "exchange-secret", server.Browser.LastVerifier);
    }

    [Fact]
    public async Task ExpiredAttemptCompletesStreamWithoutContactingProvider()
    {
        using var server = new SignalRTestServer();
        server.Browser.FailPoll = true;
        await using var hub = server.CreateHub();
        await hub.StartAsync();
        var attempt = await hub.InvokeAsync<BrowserLoginReply>("StartBrowserLogin");
        server.Clock.Advance(TimeSpan.FromMinutes(6));
        var updates = await WatchToEnd(hub, attempt.AttemptId);
        Assert.Equal(WireBrowserState.Expired, updates[^1].State);
        Assert.Empty(server.Secrets.Values);
    }

    [Fact]
    public async Task ExplicitCancelCompletesAnActiveBrowserStream()
    {
        using var server = new SignalRTestServer();
        server.Browser.PollState = BrowserPollState.Pending;
        await using var hub = server.CreateHub();
        await hub.StartAsync();
        var attempt = await hub.InvokeAsync<BrowserLoginReply>("StartBrowserLogin");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var stream = hub.StreamAsync<BrowserLoginReply>("WatchBrowserLogin", attempt.AttemptId,
            deadline.Token).GetAsyncEnumerator();
        Assert.True(await stream.MoveNextAsync());
        Assert.Equal(WireBrowserState.Waiting, stream.Current.State);
        var canceled = await hub.InvokeAsync<BrowserLoginReply>("CancelBrowserLogin", attempt.AttemptId);
        Assert.Equal(WireBrowserState.Canceled, canceled.State);
        var terminal = WireBrowserState.Waiting;
        while (await stream.MoveNextAsync()) terminal = stream.Current.State;
        Assert.Equal(WireBrowserState.Canceled, terminal);
        Assert.Empty(server.Secrets.Values);
    }

    [Fact]
    public async Task BrowserProviderExceptionsAreSafeTerminalResults()
    {
        using var server = new SignalRTestServer();
        server.Browser.FailPoll = true;
        await using var hub = server.CreateHub();
        await hub.StartAsync();
        var attempt = await hub.InvokeAsync<BrowserLoginReply>("StartBrowserLogin");
        var updates = await WatchToEnd(hub, attempt.AttemptId);
        Assert.Equal(WireBrowserState.Failed, updates[^1].State);
        Assert.DoesNotContain("sensitive-provider-value", updates[^1].Message);
        server.Browser.FailStart = true;
        var exception = await Assert.ThrowsAsync<HubException>(() => hub.InvokeAsync<BrowserLoginReply>("StartBrowserLogin"));
        Assert.DoesNotContain("sensitive-provider-value", exception.ToString());
        server.AssertResponsesExclude("sensitive-provider-value", "exchange-secret");
    }

    [Fact]
    public async Task BrowserLoginCapacityReturnsUsefulSafeErrorAndAllowsRetryAfterCancel()
    {
        using var server = new SignalRTestServer();
        await using var hub = server.CreateHub();
        await hub.StartAsync();
        var first = await hub.InvokeAsync<BrowserLoginReply>("StartBrowserLogin");
        for (var i = 1; i < 32; i++)
        {
            await hub.InvokeAsync<BrowserLoginReply>("StartBrowserLogin");
        }

        var exception = await Assert.ThrowsAsync<HubException>(() =>
            hub.InvokeAsync<BrowserLoginReply>("StartBrowserLogin"));

        Assert.Contains("Too many pending browser logins. Cancel an existing login or wait for it to expire.",
            exception.Message);
        Assert.Equal(32, server.Browser.StartCount);
        await hub.InvokeAsync<BrowserLoginReply>("CancelBrowserLogin", first.AttemptId);
        var retry = await hub.InvokeAsync<BrowserLoginReply>("StartBrowserLogin");
        Assert.Equal(WireBrowserState.Waiting, retry.State);
        Assert.Equal(33, server.Browser.StartCount);
        server.AssertResponsesExclude("exchange-secret", "browser-key");
    }

    [Fact]
    public async Task UnknownAttemptReturnsSafeStreamError()
    {
        using var server = new SignalRTestServer();
        await using var hub = server.CreateHub();
        await hub.StartAsync();
        await Assert.ThrowsAsync<HubException>(() => WatchToEnd(hub, "unknown-attempt-secret"));
        server.AssertResponsesExclude("unknown-attempt-secret");
    }

    [Fact]
    public async Task DisconnectDoesNotCancelServerOwnedAttemptAndAnotherClientCanResume()
    {
        using var server = new SignalRTestServer();
        server.Browser.PollState = BrowserPollState.Pending;
        string attemptId;
        await using (var first = server.CreateHub())
        {
            await first.StartAsync();
            attemptId = (await first.InvokeAsync<BrowserLoginReply>("StartBrowserLogin")).AttemptId;
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await using var stream = first.StreamAsync<BrowserLoginReply>("WatchBrowserLogin", attemptId,
                deadline.Token).GetAsyncEnumerator();
            Assert.True(await stream.MoveNextAsync());
            Assert.Equal(WireBrowserState.Waiting, stream.Current.State);
            await first.StopAsync();
        }
        server.Browser.PollState = BrowserPollState.Completed;
        server.Clock.Advance(TimeSpan.FromSeconds(4));
        await using var second = server.CreateHub();
        await second.StartAsync();
        var updates = await WatchToEnd(second, attemptId);
        Assert.Equal(WireBrowserState.Completed, updates[^1].State);
        Assert.Contains("browser-key", server.Secrets.Values.Values);
    }

    [Fact]
    public async Task CancelingOnlyTheStreamKeepsAttemptAvailableForAnotherWatcher()
    {
        using var server = new SignalRTestServer();
        server.Browser.PollState = BrowserPollState.Pending;
        await using var hub = server.CreateHub();
        await hub.StartAsync();
        var attempt = await hub.InvokeAsync<BrowserLoginReply>("StartBrowserLogin");
        using (var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
        {
            await using var stream = hub.StreamAsync<BrowserLoginReply>("WatchBrowserLogin", attempt.AttemptId,
                cancellation.Token).GetAsyncEnumerator();
            Assert.True(await stream.MoveNextAsync());
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await stream.MoveNextAsync());
        }
        server.Browser.PollState = BrowserPollState.Completed;
        server.Clock.Advance(TimeSpan.FromSeconds(4));
        var updates = await WatchToEnd(hub, attempt.AttemptId);
        Assert.Equal(WireBrowserState.Completed, updates[^1].State);
    }

    [Fact]
    public async Task DisconnectDuringCredentialExchangeDoesNotLoseServerLogin()
    {
        using var server = new SignalRTestServer();
        server.Browser.ExchangeResult = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var first = server.CreateHub();
        await first.StartAsync();
        var attempt = await first.InvokeAsync<BrowserLoginReply>("StartBrowserLogin");
        var watcher = WatchToEnd(first, attempt.AttemptId);
        await server.Browser.ExchangeStarted.Task.WaitAsync(TimeSpan.FromSeconds(15));
        await first.StopAsync();
        server.Browser.ExchangeResult.SetResult("browser-key");
        try { await watcher; } catch (Exception e) when (e is OperationCanceledException or IOException or HubException) { }
        await using var second = server.CreateHub();
        await second.StartAsync();
        var updates = await WatchToEnd(second, attempt.AttemptId);
        Assert.Equal(WireBrowserState.Completed, updates[^1].State);
        Assert.Contains("browser-key", server.Secrets.Values.Values);
        server.AssertResponsesExclude("browser-key", "exchange-secret", server.Browser.LastVerifier);
    }

    private static async Task<List<BrowserLoginReply>> WatchToEnd(HubConnection hub, string attemptId)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var results = new List<BrowserLoginReply>();
        await foreach (var update in hub.StreamAsync<BrowserLoginReply>("WatchBrowserLogin", attemptId, deadline.Token))
            results.Add(update);
        Assert.NotEmpty(results);
        return results;
    }
}
