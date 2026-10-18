using System.Diagnostics;
using VibeRe.Client.Application;
using Protocol = VibeRe.Contracts;

namespace VibeRe.Client.Tests;

public sealed class ClientCancellationTests
{
    [Fact]
    public async Task CallerCancellationBoundsBrowserCleanupAndPreservesCallerCancellation()
    {
        var watchStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var server = UnresponsiveCleanupServer();
        server.Scenario.Watch = (_, ct) => TestAuthScenario.WaitForCancellation(watchStarted, ct);
        using var client = server.CreateClient();
        using var cancellation = new CancellationTokenSource();
        var login = client.LoginWithBrowserAsync(_ => Task.CompletedTask, cancellation.Token);
        await watchStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var elapsed = Stopwatch.StartNew();

        cancellation.Cancel();

        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => login.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(cancellation.Token, error.CancellationToken);
        AssertBoundedCleanup(server, elapsed);
    }

    [Fact]
    public async Task BrowserFailureSurvivesUnresponsiveCleanup()
    {
        using var server = UnresponsiveCleanupServer();
        using var client = server.CreateClient();
        var original = new InvalidOperationException("Browser callback failed.");
        var elapsed = Stopwatch.StartNew();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.LoginWithBrowserAsync(_ => Task.FromException(original)).WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.Same(original, error);
        AssertBoundedCleanup(server, elapsed);
        Assert.Empty(server.Scenario.WatchedAttempts);
    }

    [Fact]
    public async Task ExpiredAttemptReturnsExpiredAfterOneBoundedCleanup()
    {
        using var server = UnresponsiveCleanupServer();
        server.Scenario.Start = _ => Task.FromResult(TestAuthScenario.StartedAttempt() with
        {
            ExpiresAtUnixSeconds = DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeSeconds()
        });
        using var client = server.CreateClient();
        var elapsed = Stopwatch.StartNew();

        var result = await client.LoginWithBrowserAsync(_ => Task.CompletedTask).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(BrowserLoginState.Expired, result.State);
        AssertBoundedCleanup(server, elapsed);
        Assert.Empty(server.Scenario.WatchedAttempts);
    }

    private static SignalRTestServer UnresponsiveCleanupServer() => new(new()
    {
        Cancel = async (_, ct) =>
        {
            // Unary SignalR cancellation stops the client wait, not the server
            // method. Connection disposal releases this deliberately stuck work.
            await Task.Delay(Timeout.Infinite, ct);
            return new Protocol.BrowserLoginReply();
        }
    });

    private static void AssertBoundedCleanup(SignalRTestServer server, Stopwatch elapsed)
    {
        Assert.Equal("attempt", Assert.Single(server.Scenario.CanceledAttempts));
        Assert.InRange(elapsed.Elapsed, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5));
    }
}
