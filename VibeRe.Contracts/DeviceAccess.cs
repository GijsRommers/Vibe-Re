namespace VibeRe.Contracts;

public sealed record AccessStatusReply(bool Authorized, bool CanManageDevices, bool IsLocal,
    string? DeviceName = null, DateTimeOffset? ExpiresAt = null);
public sealed record BeginPairingRequest(string DeviceName);
public sealed record PairingReply(string PairingId, string PairingSecret, string ApprovalCode, DateTimeOffset ExpiresAt)
{
    public override string ToString() => $"PairingReply {{ ExpiresAt = {ExpiresAt} }}";
}
public sealed record RedeemPairingRequest(string PairingId, string PairingSecret)
{
    public override string ToString() => "RedeemPairingRequest { [redacted] }";
}
public sealed record ApprovePairingRequest(string ApprovalCode);
public sealed record DeviceReply(Guid Id, string Name, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt,
    DateTimeOffset? RevokedAt = null);
public sealed record DeviceTokenReply(string Token, DeviceReply Device)
{
    public override string ToString() => $"DeviceTokenReply {{ Device = {Device} }}";
}
