namespace VibeRe.Client.Configuration;

public sealed class LocalServerMonitor(IServerReadinessProbe probe)
{
    public async Task RunAsync(Func<ServerConnection?> selectedServer,
        Func<CancellationToken, Task> reconnect, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                if (selectedServer() is not { StartLocally: true } server) continue;
                if (!await probe.IsReadyAsync(new Uri(server.Address), cancellationToken))
                    await reconnect(cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }
}
