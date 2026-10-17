using VibeRe.Runtime.Adapters;
using System.Net;
using System.Net.Sockets;
using VibeRe.Runtime.Configuration;

namespace VibeRe.Client.Tests;

public class LocalServerLauncherTests
{
    private static LocalServerLauncher CreateLauncher() => new(new ServerReadinessProbe(), new LocalServerProcess());

    [Fact]
    public async Task RemoteAndConnectOnlyProfilesNeverLaunchLocalServer()
    {
        var probe = new Probe();
        var process = new Process();
        var launcher = new LocalServerLauncher(probe, process);
        await launcher.EnsureRunningAsync(new(Guid.NewGuid(), "remote", "https://offline.example"));
        await launcher.EnsureRunningAsync(new(Guid.NewGuid(), "local", "http://localhost:1", StartLocally: false));
        Assert.Equal(0, probe.ReadinessChecks);
        Assert.Equal(0, process.Starts);
    }

    [Fact]
    public async Task EnabledRemoteStartupIsRejected()
    {
        var probe = new Probe();
        var process = new Process();
        await Assert.ThrowsAsync<ArgumentException>(() => new LocalServerLauncher(probe, process).EnsureRunningAsync(
            new(Guid.NewGuid(), "remote", "https://remote.example", StartLocally: true)));
        Assert.Equal(0, probe.ReadinessChecks);
        Assert.Equal(0, process.Starts);
    }

    [Fact]
    public async Task OccupiedPortDoesNotGetMistakenForVibeRe()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var error = await Assert.ThrowsAsync<LocalServerStartupException>(() => CreateLauncher().EnsureRunningAsync(
            new(Guid.NewGuid(), "other-service", $"http://127.0.0.1:{port}", StartLocally: true)));
        Assert.Contains("occupied", error.Message);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task ExistingReadyServerIsReusedWithoutLaunching(int readyOnCheck)
    {
        var probe = new Probe((check, _) => Task.FromResult(check >= readyOnCheck));
        var process = new Process();
        await new LocalServerLauncher(probe, process).EnsureRunningAsync(LocalServer());
        Assert.Equal(readyOnCheck, probe.ReadinessChecks);
        Assert.Equal(0, process.Starts);
    }

    [Fact]
    public async Task LaunchWaitsForCompatibleReadiness()
    {
        var probe = new Probe((check, _) => Task.FromResult(check >= 4));
        var process = new Process();
        var server = LocalServer();
        await new LocalServerLauncher(probe, process).EnsureRunningAsync(server);
        Assert.Equal(1, process.Starts);
        Assert.Equal(server, process.StartedServer);
        Assert.Equal(4, probe.ReadinessChecks);
    }

    [Fact]
    public async Task CancellationBeforeStartupDoesNotProbeOrLaunch()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var probe = new Probe();
        var process = new Process();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new LocalServerLauncher(probe, process).EnsureRunningAsync(LocalServer(), cancellation.Token));
        Assert.Equal(0, probe.ReadinessChecks);
        Assert.Equal(0, process.Starts);
    }

    [Fact]
    public async Task CancellationDuringReadinessIsNotReportedAsTimeout()
    {
        using var cancellation = new CancellationTokenSource();
        var process = new Process(cancellation.Cancel);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new LocalServerLauncher(new Probe(), process).EnsureRunningAsync(LocalServer(), cancellation.Token));
        Assert.Equal(1, process.Starts);
    }

    [Fact]
    public async Task TimeoutIncludesInitialReadinessProbe()
    {
        var probe = new Probe(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return false;
        });
        var process = new Process();
        var error = await Assert.ThrowsAsync<LocalServerStartupException>(() =>
            new LocalServerLauncher(probe, process, TimeSpan.FromMilliseconds(50)).EnsureRunningAsync(LocalServer()));
        Assert.Contains("did not start in time", error.Message);
        Assert.Equal(0, process.Starts);
    }

    [Fact]
    public async Task ReadinessTimeoutIsBoundedAndReleasesLaunchLock()
    {
        var server = LocalServer();
        var process = new Process();
        var error = await Assert.ThrowsAsync<LocalServerStartupException>(() =>
            new LocalServerLauncher(new Probe(), process, TimeSpan.FromMilliseconds(500)).EnsureRunningAsync(server));
        Assert.Contains("did not start in time", error.Message);
        Assert.Equal(1, process.Starts);
        // The failed attempt must not hold the cross-client startup lease.
        var retryProbe = new Probe((check, _) => Task.FromResult(check >= 3));
        await new LocalServerLauncher(retryProbe, process, TimeSpan.FromSeconds(1)).EnsureRunningAsync(server);
        Assert.Equal(2, process.Starts);
    }

    private static ServerConnection LocalServer() =>
        new(Guid.NewGuid(), "local", $"http://127.0.0.1:{Random.Shared.Next(20000, 60000)}", StartLocally: true);

    private sealed class Probe(Func<int, CancellationToken, Task<bool>>? readiness = null) : IServerReadinessProbe
    {
        public int ReadinessChecks { get; private set; }
        public Task<bool> IsReadyAsync(Uri address, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadinessChecks++;
            return readiness?.Invoke(ReadinessChecks, cancellationToken) ?? Task.FromResult(false);
        }
        public Task<bool> IsListeningAsync(Uri address, CancellationToken cancellationToken = default) => Task.FromResult(false);
    }

    private sealed class Process(Action? onStart = null) : ILocalServerProcess
    {
        public int Starts { get; private set; }
        public ServerConnection? StartedServer { get; private set; }
        public void Start(ServerConnection server, string logPath)
        {
            Starts++;
            StartedServer = server;
            onStart?.Invoke();
        }
    }
}
