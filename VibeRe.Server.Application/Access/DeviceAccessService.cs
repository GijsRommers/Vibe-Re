using System.Security.Cryptography;
using System.Text;
using VibeRe.Domain.Access;

namespace VibeRe.Server.Application.Access;

// Register as a singleton: pairing state is short-lived and intentionally not persisted.
// A restart invalidates pending pairings but leaves issued, hashed sessions intact.
public sealed class DeviceAccessService(IDeviceSessionStore store, TimeProvider time)
    : IDeviceAccessService, IDeviceSessionIssuer
{
    private const int MaxPendingPairings = 32;
    private const int MaxApprovalAttemptsPerMinute = 10;
    private const string CodeAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
    private static readonly TimeSpan PairingLifetime = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan SessionLifetime = TimeSpan.FromDays(30);
    private readonly SemaphoreSlim _pairingGate = new(1, 1);
    private readonly Dictionary<string, PendingPairing> _pairings = new(StringComparer.Ordinal);
    private readonly Queue<DateTimeOffset> _approvalAttempts = new();

    public async Task<DevicePairingRequest> BeginPairingAsync(string deviceName,
        CancellationToken cancellationToken = default)
    {
        var name = ValidateDeviceName(deviceName);
        await _pairingGate.WaitAsync(cancellationToken);
        try
        {
            var now = time.GetUtcNow();
            RemoveExpiredPairings(now);
            if (_pairings.Count >= MaxPendingPairings)
                throw new DeviceAccessLimitException("Too many pending device pairings. Try again later.");
            var id = NewSecret();
            var secret = NewSecret();
            string code;
            do { code = NewApprovalCode(); }
            while (_pairings.Values.Any(x => x.ApprovalCode == code));
            var expiresAt = now.Add(PairingLifetime);
            _pairings.Add(id, new(name, Hash(secret), code, expiresAt));
            return new(id, secret, code, expiresAt);
        }
        finally { _pairingGate.Release(); }
    }

    public async Task<bool> ApprovePairingAsync(string approvalCode,
        CancellationToken cancellationToken = default)
    {
        await _pairingGate.WaitAsync(cancellationToken);
        try
        {
            var now = time.GetUtcNow();
            RemoveExpiredPairings(now);
            while (_approvalAttempts.TryPeek(out var attempt) && attempt <= now.AddMinutes(-1))
                _approvalAttempts.Dequeue();
            if (_approvalAttempts.Count >= MaxApprovalAttemptsPerMinute)
                throw new DeviceAccessLimitException("Too many pairing approval attempts. Try again later.");
            _approvalAttempts.Enqueue(now);
            var code = NormalizeApprovalCode(approvalCode);
            if (code is null) return false;
            var pairing = _pairings.Values.SingleOrDefault(x => x.ApprovalCode == code);
            if (pairing is null || pairing.Approved) return false;
            pairing.Approved = true;
            return true;
        }
        finally { _pairingGate.Release(); }
    }

    public async Task<DevicePairingRedemption> RedeemPairingAsync(string pairingId, string pairingSecret,
        CancellationToken cancellationToken = default)
    {
        string deviceName;
        await _pairingGate.WaitAsync(cancellationToken);
        try
        {
            RemoveExpiredPairings(time.GetUtcNow());
            if (!IsSecret(pairingId) || !IsSecret(pairingSecret) ||
                !_pairings.TryGetValue(pairingId, out var pairing) ||
                !CryptographicOperations.FixedTimeEquals(Hash(pairingSecret), pairing.SecretHash))
                return new(DevicePairingState.Unavailable);
            if (!pairing.Approved) return new(DevicePairingState.Pending);
            deviceName = pairing.DeviceName;
            // Consume before persistence: concurrent requests, cancellation or an ambiguous store
            // failure cannot issue a second session from this authorization. Start a new pairing
            // if the successful response is lost; no API can retrieve a raw token afterward.
            _pairings.Remove(pairingId);
        }
        finally { _pairingGate.Release(); }
        return new(DevicePairingState.Completed, await IssueSessionAsync(deviceName, SessionLifetime,
            DevicePermissions.ProviderAccess, cancellationToken));
    }

    public async Task<DeviceSessionGrant> IssueSessionAsync(string deviceName, TimeSpan lifetime,
        DevicePermissions permissions, CancellationToken cancellationToken = default)
    {
        var name = ValidateDeviceName(deviceName);
        if (lifetime <= TimeSpan.Zero || lifetime > SessionLifetime)
            throw new ArgumentOutOfRangeException(nameof(lifetime), "Session lifetime must be positive and no longer than 30 days.");
        if (!permissions.HasFlag(DevicePermissions.ProviderAccess) ||
            (permissions & ~(DevicePermissions.ProviderAccess | DevicePermissions.ManageDevices)) != 0)
            throw new ArgumentOutOfRangeException(nameof(permissions), "Invalid device permissions.");
        cancellationToken.ThrowIfCancellationRequested();
        var token = NewSecret();
        var now = time.GetUtcNow();
        var session = new DeviceSession
        {
            Id = Guid.NewGuid(), Name = name, TokenHash = Convert.ToHexString(Hash(token)),
            CreatedAt = now, ExpiresAt = now.Add(lifetime), Permissions = permissions
        };
        await store.CreateAsync(session, cancellationToken);
        return new(token, Describe(session));
    }

    public async Task<DeviceInfo?> ValidateSessionAsync(string token,
        CancellationToken cancellationToken = default)
    {
        if (!IsSecret(token)) return null;
        var session = await store.FindByTokenHashAsync(Convert.ToHexString(Hash(token)), cancellationToken);
        // No authentication cache: hosts may call this on every invocation and stream event.
        return session is not null && session.RevokedAt is null && session.ExpiresAt > time.GetUtcNow()
            ? Describe(session) : null;
    }

    public async Task<IReadOnlyList<DeviceInfo>> ListDevicesAsync(CancellationToken cancellationToken = default)
        => (await store.ListAsync(cancellationToken)).Select(Describe).OrderByDescending(x => x.CreatedAt).ToArray();

    public Task<bool> RevokeDeviceAsync(Guid deviceId, CancellationToken cancellationToken = default)
        => store.RevokeAsync(deviceId, time.GetUtcNow(), cancellationToken);

    private void RemoveExpiredPairings(DateTimeOffset now)
    {
        foreach (var id in _pairings.Where(x => x.Value.ExpiresAt <= now).Select(x => x.Key).ToArray())
            _pairings.Remove(id);
    }

    private static string ValidateDeviceName(string deviceName)
    {
        if (string.IsNullOrWhiteSpace(deviceName) || deviceName.Length > 100 || deviceName.Any(char.IsControl))
            throw new ArgumentException("Device name must contain 1 to 100 printable characters.", nameof(deviceName));
        return deviceName.Trim();
    }

    private static string? NormalizeApprovalCode(string approvalCode)
    {
        if (string.IsNullOrWhiteSpace(approvalCode) || approvalCode.Length > 32) return null;
        var code = approvalCode.Replace("-", "", StringComparison.Ordinal)
            .Replace(" ", "", StringComparison.Ordinal).ToUpperInvariant();
        return code.Length == 8 && code.All(CodeAlphabet.Contains) ? code : null;
    }

    private static string NewApprovalCode() => string.Create(8, 0, (characters, _) =>
    {
        for (var index = 0; index < characters.Length; index++)
            characters[index] = CodeAlphabet[RandomNumberGenerator.GetInt32(CodeAlphabet.Length)];
    });

    private static string NewSecret() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static bool IsSecret(string? secret) => secret is { Length: 43 } &&
        secret.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
    private static byte[] Hash(string secret) => SHA256.HashData(Encoding.ASCII.GetBytes(secret));
    private static DeviceInfo Describe(DeviceSession session)
        => new(session.Id, session.Name, session.CreatedAt, session.ExpiresAt, session.RevokedAt, session.Permissions);

    private sealed class PendingPairing(string deviceName, byte[] secretHash, string approvalCode,
        DateTimeOffset expiresAt)
    {
        public string DeviceName { get; } = deviceName;
        public byte[] SecretHash { get; } = secretHash;
        public string ApprovalCode { get; } = approvalCode;
        public DateTimeOffset ExpiresAt { get; } = expiresAt;
        public bool Approved { get; set; }
    }
}
