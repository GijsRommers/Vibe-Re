using VibeRe.Domain.Access;

namespace VibeRe.Server.Application.Access;

// Trusted, in-process bootstrap port only. It must never be exposed directly to a client.
// A bootstrap adapter authorizes its own request before calling the same issuer.
public interface IDeviceSessionIssuer
{
    Task<DeviceSessionGrant> IssueSessionAsync(string deviceName, TimeSpan lifetime, DevicePermissions permissions,
        CancellationToken cancellationToken = default);
}
