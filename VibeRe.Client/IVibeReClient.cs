using VibeRe.Client.Configuration;
using VibeRe.Protocol;

namespace VibeRe.Client;

public interface IVibeReClient : IDisposable
{
    ServerConnection Server { get; }
    Task<AuthStatusReply> LoginAsync(string apiKey, CancellationToken cancellationToken = default);
    Task<AuthStatusReply> GetStatusAsync(CancellationToken cancellationToken = default);
    Task<AuthStatusReply> LogoutAsync(CancellationToken cancellationToken = default);
    Task<BrowserLoginReply> StartBrowserLoginAsync(CancellationToken cancellationToken = default);
    Task<BrowserLoginReply> PollBrowserLoginAsync(string attemptId, CancellationToken cancellationToken = default);
    Task<BrowserLoginReply> CancelBrowserLoginAsync(string attemptId, CancellationToken cancellationToken = default);
    Task<BrowserLoginReply> LoginWithBrowserAsync(Func<BrowserLoginReply, Task> showBrowser,
        CancellationToken cancellationToken = default);
}
