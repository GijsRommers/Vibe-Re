using System.Net;
using System.Net.Sockets;
using VibeRe.Client.Configuration;

namespace VibeRe.Client.Tests;

public class LocalServerLauncherTests
{
    private static LocalServerLauncher CreateLauncher() => new(new ServerReadinessProbe(), new LocalServerProcess());

    [Fact]
    public async Task RemoteAndConnectOnlyProfilesNeverLaunchLocalServer()
    {
        var launcher = CreateLauncher();
        await launcher.EnsureRunningAsync(new(Guid.NewGuid(), "remote", "https://offline.example"));
        await launcher.EnsureRunningAsync(new(Guid.NewGuid(), "local", "http://localhost:1", StartLocally: false));
    }

    [Fact]
    public async Task EnabledRemoteStartupIsRejected()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => CreateLauncher().EnsureRunningAsync(
            new(Guid.NewGuid(), "remote", "https://remote.example", StartLocally: true)));
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
}
