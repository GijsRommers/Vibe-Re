namespace VibeRe.Server.Application.Access;

// Host adapters authenticate approval/list/revocation and rate-limit anonymous pairing endpoints.
public interface IDeviceAccessService
{
    Task<DevicePairingRequest> BeginPairingAsync(string deviceName, CancellationToken cancellationToken = default);
    Task<bool> ApprovePairingAsync(string approvalCode, CancellationToken cancellationToken = default);
    Task<DevicePairingRedemption> RedeemPairingAsync(string pairingId, string pairingSecret,
        CancellationToken cancellationToken = default);
    Task<DeviceInfo?> ValidateSessionAsync(string token, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DeviceInfo>> ListDevicesAsync(CancellationToken cancellationToken = default);
    Task<bool> RevokeDeviceAsync(Guid deviceId, CancellationToken cancellationToken = default);
}
