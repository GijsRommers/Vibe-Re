namespace VibeRe.Runtime.Configuration;

public interface ILocalServerProcess
{
    void Start(ServerConnection server, string logPath);
}
