using VibeRe.Domain.Access;

namespace VibeRe.Server.Application.Access;

public sealed record DevicePairingRequest(string PairingId, string PairingSecret, string ApprovalCode,
    DateTimeOffset ExpiresAt)
{
    public override string ToString() => $"DevicePairingRequest {{ PairingId = {PairingId}, ExpiresAt = {ExpiresAt} }}";
}

public enum DevicePairingState { Pending, Completed, Unavailable }

public sealed record DevicePairingRedemption(DevicePairingState State, DeviceSessionGrant? Grant = null);

public sealed record DeviceSessionGrant(string Token, DeviceInfo Device)
{
    public override string ToString() => $"DeviceSessionGrant {{ Device = {Device} }}";
}

public sealed record DeviceInfo(Guid Id, string Name, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt,
    DateTimeOffset? RevokedAt = null, DevicePermissions Permissions = DevicePermissions.ProviderAccess);

public sealed class DeviceAccessLimitException(string message) : InvalidOperationException(message);
