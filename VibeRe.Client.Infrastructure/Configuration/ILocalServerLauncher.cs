namespace VibeRe.Client.Infrastructure.Configuration;

public interface ILocalServerLauncher
{
    Task EnsureRunningAsync(ServerConnection server, CancellationToken cancellationToken = default);
}
