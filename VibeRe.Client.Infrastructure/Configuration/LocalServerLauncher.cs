using System.Security.Cryptography;
using System.Text;
using VibeRe.Client.Infrastructure.Adapters;

namespace VibeRe.Client.Infrastructure.Configuration;

public sealed class LocalServerLauncher(IServerReadinessProbe probe, ILocalServerProcess process,
    TimeSpan? startupTimeout = null) : ILocalServerLauncher
{
    private static readonly TimeSpan DefaultStartupTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan ReadinessPollInterval = TimeSpan.FromMilliseconds(100);

    public async Task EnsureRunningAsync(ServerConnection server, CancellationToken cancellationToken = default)
    {
        if (!server.StartLocally) return;
        var uri = new Uri(ServerAddress.Normalize(server.Address));
        if (!ServerAddress.IsLoopback(server.Address)) throw new ArgumentException("Only loopback servers can be started locally.");
        if (!NativeClientPlatform.Capabilities.SupportsLocalServer)
            throw new LocalServerStartupException("Automatic server startup is available on desktop. Choose a reachable server on this device.");
        cancellationToken.ThrowIfCancellationRequested();

        var directory = NativeClientPlatform.DataDirectory;
        var logPath = Environment.GetEnvironmentVariable("VIBE_RE_SERVER_LOG") ?? Path.Combine(directory, "server.log");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(startupTimeout ?? DefaultStartupTimeout);
        var ct = timeout.Token;
        try
        {
            if (await probe.IsReadyAsync(uri, ct)) return;
            Directory.CreateDirectory(directory);
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(server.Address)))[..16];
            var lockPath = Path.Combine(directory, $"local-server-{hash}.lock");
            await using var lease = await AcquireAsync(lockPath, uri, ct);
            // Check after taking the launch lock: another client may already be starting this server.
            if (lease is null || await probe.IsReadyAsync(uri, ct)) return;
            if (await probe.IsListeningAsync(uri, ct))
                throw new LocalServerStartupException("The local server port is occupied, but a compatible VibeRe server is not responding.");

            ct.ThrowIfCancellationRequested();
            process.Start(server, logPath);
            while (!await probe.IsReadyAsync(uri, ct))
                await Task.Delay(ReadinessPollInterval, ct);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new LocalServerStartupException($"Local server did not start in time. Check {logPath}.");
        }
    }

    private async Task<FileStream?> AcquireAsync(string path, Uri uri, CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException)
            {
                if (await probe.IsReadyAsync(uri, ct)) return null;
                await Task.Delay(ReadinessPollInterval, ct);
            }
        }
    }
}
