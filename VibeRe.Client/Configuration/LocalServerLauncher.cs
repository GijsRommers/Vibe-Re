using System.Security.Cryptography;
using System.Text;

namespace VibeRe.Client.Configuration;

public sealed class LocalServerLauncher(IServerReadinessProbe probe, ILocalServerProcess process) : ILocalServerLauncher
{
    public async Task EnsureRunningAsync(ServerConnection server, CancellationToken cancellationToken = default)
    {
        if (!server.StartLocally) return;
        var uri = new Uri(ServerConnection.NormalizeAddress(server.Address));
        if (!uri.IsLoopback) throw new ArgumentException("Only loopback servers can be started locally.");
        if (OperatingSystem.IsAndroid() || OperatingSystem.IsIOS())
            throw new LocalServerStartupException("Automatic server startup is available on desktop. Choose a reachable server on this device.");
        if (await probe.IsReadyAsync(uri, cancellationToken)) return;

        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".vibe-re");
        Directory.CreateDirectory(directory);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(server.Address)))[..16];
        var lockPath = Path.Combine(directory, $"local-server-{hash}.lock");
        var deadline = DateTime.UtcNow.AddSeconds(45);
        await using var lease = await AcquireAsync(lockPath, uri, deadline, cancellationToken);
        if (lease is null || await probe.IsReadyAsync(uri, cancellationToken)) return;
        // Check after taking the launch lock: another client may already be starting this server.
        if (await probe.IsListeningAsync(uri, cancellationToken))
            throw new LocalServerStartupException("The local server port is occupied, but a compatible VibeRe server is not responding.");

        var logPath = Environment.GetEnvironmentVariable("VIBE_RE_SERVER_LOG") ?? Path.Combine(directory, "server.log");
        process.Start(server, logPath);
        while (DateTime.UtcNow < deadline)
        {
            if (await probe.IsReadyAsync(uri, cancellationToken)) return;
            await Task.Delay(100, cancellationToken);
        }
        throw new LocalServerStartupException($"Local server did not start in time. Check {logPath}.");
    }

    private async Task<FileStream?> AcquireAsync(string path, Uri uri, DateTime deadline, CancellationToken ct)
    {
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException)
            {
                if (await probe.IsReadyAsync(uri, ct)) return null;
                await Task.Delay(100, ct);
            }
        }
        throw new LocalServerStartupException("Another client is starting the local server. Try again shortly.");
    }
}
