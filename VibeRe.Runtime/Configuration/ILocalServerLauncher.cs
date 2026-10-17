namespace VibeRe.Runtime.Configuration;

public interface ILocalServerLauncher
{
    Task EnsureRunningAsync(ServerConnection server, CancellationToken cancellationToken = default);
}
