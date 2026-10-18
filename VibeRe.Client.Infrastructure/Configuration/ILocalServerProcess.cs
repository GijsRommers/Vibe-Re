namespace VibeRe.Client.Infrastructure.Configuration;

public interface ILocalServerProcess
{
    void Start(ServerConnection server, string logPath);
}
