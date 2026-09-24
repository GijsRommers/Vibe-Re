using Grpc.Net.Client;
using VibeRe.Protocol;

namespace VibeRe.Client;

/// <summary>A connection to the VibeRe server.</summary>
public sealed class VibeReClient : IDisposable
{
    /// <summary>Where the server listens unless <c>$VIBE_RE_SERVER</c> says otherwise.</summary>
    public static readonly Uri DefaultAddress = ServerDefaults.Address;

    private readonly GrpcChannel _channel;

    public VibeReClient(Uri? address = null)
    {
        Address = address ?? DefaultAddressFromEnvironment();
        _channel = GrpcChannel.ForAddress(Address);
        Auth = new AuthClient(new Protocol.Auth.AuthClient(_channel), Address);
    }

    public Uri Address { get; }

    public AuthClient Auth { get; }

    /// <summary>
    /// Starts <c>vibe-re-server</c> in the background if <see cref="Address"/> is local and nothing is listening yet.
    /// </summary>
    public Task EnsureServerRunningAsync(CancellationToken cancellationToken = default) =>
        LocalServer.EnsureRunningAsync(Address, cancellationToken);

    public void Dispose() => _channel.Dispose();

    private static Uri DefaultAddressFromEnvironment() =>
        Environment.GetEnvironmentVariable("VIBE_RE_SERVER") is { Length: > 0 } address
            ? new Uri(address)
            : DefaultAddress;
}
