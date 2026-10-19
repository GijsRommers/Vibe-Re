namespace VibeRe.Client.Infrastructure.Configuration;

public interface IServerReadinessProbe
{
    Task<bool> IsReadyAsync(Uri address, CancellationToken cancellationToken = default);
    Task<bool> IsListeningAsync(Uri address, CancellationToken cancellationToken = default);
}
