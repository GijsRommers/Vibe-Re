namespace VibeRe.Client.Configuration;

public interface ILocalServerProcess
{
    void Start(ServerConnection server, string logPath);
}
