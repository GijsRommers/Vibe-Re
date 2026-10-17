using VibeRe.Protocol;

namespace VibeRe.Client;

public interface IVibeReClient : IDisposable
{
    Task<AuthStatusReply> LoginAsync(string apiKey, CancellationToken cancellationToken = default);
    Task<AuthStatusReply> GetStatusAsync(CancellationToken cancellationToken = default);
    Task<AuthStatusReply> LogoutAsync(CancellationToken cancellationToken = default);
    Task<BrowserLoginReply> LoginWithBrowserAsync(Func<BrowserLoginReply, Task> showBrowser,
        CancellationToken cancellationToken = default);
}
