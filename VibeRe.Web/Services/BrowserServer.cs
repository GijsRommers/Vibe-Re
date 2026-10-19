using VibeRe.Client.Application;
using VibeRe.Client.Application.Configuration;

namespace VibeRe.Web.Services;

// The document origin is the only server this browser process can contact.
// Choosing another server opens its own web app and its own cookie boundary.
public sealed class BrowserServer
{
    public BrowserServer(string documentAddress)
    {
        var document = new Uri(documentAddress, UriKind.Absolute);
        Origin = new Uri(ServerAddress.Normalize(document.GetLeftPart(UriPartial.Authority)) + "/");
        Connection = new ServerConnection(Guid.Empty, "current", Origin.GetLeftPart(UriPartial.Authority));
    }

    public Uri Origin { get; }
    public ServerConnection Connection { get; }

    public static string NavigationTarget(string address)
    {
        var origin = ServerAddress.Normalize(address);
        if (new Uri(origin).Scheme != Uri.UriSchemeHttps)
            throw new ArgumentException("Use an HTTPS server address. Open this computer’s local server directly in your browser.");
        return origin + "/";
    }
}

public sealed class CurrentOriginConfiguration(BrowserServer server) : IClientConfigurationStore
{
    // Return a fresh collection so callers cannot mutate the next startup's configuration.
    public Task<ClientConfiguration> LoadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new ClientConfiguration("current", [server.Connection]));
    }

    public Task<ClientConfiguration> UpdateAsync(Func<ClientConfiguration, Task<ClientConfiguration>> update,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("The web client always uses its document origin. Navigate to another server to change it.");
}
