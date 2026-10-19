using VibeRe.Domain.Access;

namespace VibeRe.Server.Application.Access;

public interface IDeviceSessionStore
{
    Task CreateAsync(DeviceSession session, CancellationToken cancellationToken = default);
    Task<DeviceSession?> FindByTokenHashAsync(string tokenHash, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DeviceSession>> ListAsync(CancellationToken cancellationToken = default);
    Task<bool> RevokeAsync(Guid deviceId, DateTimeOffset revokedAt, CancellationToken cancellationToken = default);
}
