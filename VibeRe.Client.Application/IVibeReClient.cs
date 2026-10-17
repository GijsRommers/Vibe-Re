namespace VibeRe.Client.Application;

public interface IVibeReClient : IDisposable
{
    Task<AuthStatus> LoginAsync(string apiKey, CancellationToken cancellationToken = default);
    Task<AuthStatus> GetStatusAsync(CancellationToken cancellationToken = default);
    Task<AuthStatus> LogoutAsync(CancellationToken cancellationToken = default);
    Task<BrowserLoginResult> LoginWithBrowserAsync(Func<BrowserLoginResult, Task> showBrowser,
        CancellationToken cancellationToken = default);
}
