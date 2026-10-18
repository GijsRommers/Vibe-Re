namespace VibeRe.Client.Application;

// Native hosts receive only the approval challenge and the final server-access token.
// Pending pairing credentials stay in memory inside the transport adapter.
public interface IDevicePairingClient
{
    Task<string> PairAsync(string serverAddress, string deviceName,
        Func<DevicePairingChallenge, Task> showChallenge, CancellationToken cancellationToken = default);
}

public sealed record DevicePairingChallenge(string ApprovalCode, DateTimeOffset ExpiresAt);
