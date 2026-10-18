using System.Security.Cryptography;
using System.Text;
using VibeRe.Domain.Access;
using VibeRe.Server.Application.Access;

namespace VibeRe.Server.Application.Tests;

public sealed class DeviceAccessServiceTests
{
    [Fact]
    public async Task PairingRequiresApprovalAndMatchingSecretAndReturnsRawTokenOnlyOnce()
    {
        var f = new Fixture();
        var pairing = await f.Service.BeginPairingAsync("  My tablet  ");
        Assert.Equal(f.Clock.Now.AddMinutes(5), pairing.ExpiresAt);
        Assert.Equal(43, pairing.PairingId.Length);
        Assert.Equal(43, pairing.PairingSecret.Length);
        Assert.NotEqual(pairing.PairingId, pairing.PairingSecret);
        Assert.Matches("^[A-HJ-NP-Z2-9]{8}$", pairing.ApprovalCode);
        var pending = await f.Service.RedeemPairingAsync(pairing.PairingId, pairing.PairingSecret);
        Assert.Equal(DevicePairingState.Pending, pending.State);
        Assert.Null(pending.Grant);
        Assert.Empty(f.Store.Sessions);
        Assert.True(await f.Service.ApprovePairingAsync(pairing.ApprovalCode.ToLowerInvariant().Insert(4, "-")));
        Assert.False(await f.Service.ApprovePairingAsync(pairing.ApprovalCode));
        Assert.Equal(DevicePairingState.Unavailable,
            (await f.Service.RedeemPairingAsync(pairing.PairingId, new string('a', 43))).State);
        var redeemed = await f.Service.RedeemPairingAsync(pairing.PairingId, pairing.PairingSecret);
        Assert.Equal(DevicePairingState.Completed, redeemed.State);
        var grant = Assert.IsType<DeviceSessionGrant>(redeemed.Grant);
        Assert.Equal("My tablet", grant.Device.Name);
        Assert.Equal(f.Clock.Now.AddDays(30), grant.Device.ExpiresAt);
        Assert.Equal(DevicePermissions.ProviderAccess, grant.Device.Permissions);
        Assert.Equal(grant.Device, await f.Service.ValidateSessionAsync(grant.Token));
        var consumed = await f.Service.RedeemPairingAsync(pairing.PairingId, pairing.PairingSecret);
        Assert.Equal(DevicePairingState.Unavailable, consumed.State);
        Assert.Null(consumed.Grant);
        Assert.Single(f.Store.Sessions);
        Assert.DoesNotContain(grant.Token, grant.ToString());
        Assert.DoesNotContain(pairing.PairingSecret, pairing.ToString());
        Assert.DoesNotContain(pairing.ApprovalCode, pairing.ToString());
    }

    [Fact]
    public async Task ConcurrentRedemptionsIssueExactlyOneSession()
    {
        var f = new Fixture();
        var pairing = await f.Service.BeginPairingAsync("browser");
        await f.Service.ApprovePairingAsync(pairing.ApprovalCode);
        var results = await Task.WhenAll(Enumerable.Range(0, 20)
            .Select(_ => Task.Run(() => f.Service.RedeemPairingAsync(pairing.PairingId, pairing.PairingSecret))));
        Assert.Single(results, x => x.State == DevicePairingState.Completed);
        Assert.Equal(19, results.Count(x => x.State == DevicePairingState.Unavailable));
        Assert.Single(f.Store.Sessions);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PairingsExpireAtExactBoundaryWhetherApprovedOrNot(bool approve)
    {
        var f = new Fixture();
        var pairing = await f.Service.BeginPairingAsync("browser");
        if (approve) await f.Service.ApprovePairingAsync(pairing.ApprovalCode);
        f.Clock.Now = pairing.ExpiresAt;
        Assert.False(await f.Service.ApprovePairingAsync(pairing.ApprovalCode));
        var result = await f.Service.RedeemPairingAsync(pairing.PairingId, pairing.PairingSecret);
        Assert.Equal(DevicePairingState.Unavailable, result.State);
        Assert.Null(result.Grant);
        Assert.Empty(f.Store.Sessions);
    }

    [Fact]
    public async Task PairingsAreBoundedEvenUnderConcurrentRequestsAndExpiryReclaimsCapacity()
    {
        var f = new Fixture();
        await Task.WhenAll(Enumerable.Range(0, 32)
            .Select(_ => Task.Run(() => f.Service.BeginPairingAsync("browser"))));
        await Assert.ThrowsAsync<DeviceAccessLimitException>(() => f.Service.BeginPairingAsync("one too many"));
        f.Clock.Now = f.Clock.Now.AddMinutes(5);
        Assert.NotNull(await f.Service.BeginPairingAsync("new browser"));
    }

    [Fact]
    public async Task ApprovalBruteForceIsBoundedAndWindowExpires()
    {
        var f = new Fixture();
        var pairing = await f.Service.BeginPairingAsync("browser");
        for (var i = 0; i < 10; i++) Assert.False(await f.Service.ApprovePairingAsync("invalid"));
        await Assert.ThrowsAsync<DeviceAccessLimitException>(() => f.Service.ApprovePairingAsync(pairing.ApprovalCode));
        f.Clock.Now = f.Clock.Now.AddMinutes(1);
        Assert.True(await f.Service.ApprovePairingAsync(pairing.ApprovalCode));
    }

    [Fact]
    public async Task OnlyHashesArePersistedAndMetadataDoesNotExposeCredentials()
    {
        var f = new Fixture();
        var first = await f.IssueAsync("first");
        var second = await f.IssueAsync("second");
        Assert.NotEqual(first.Token, second.Token);
        Assert.Equal(43, first.Token.Length);
        var stored = f.Store.Sessions[first.Device.Id];
        Assert.Equal(Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(first.Token))), stored.TokenHash);
        Assert.DoesNotContain(first.Token, stored.TokenHash);
        var devices = await f.Service.ListDevicesAsync();
        Assert.Equal(2, devices.Count);
        Assert.DoesNotContain(first.Token, string.Join(",", devices));
        Assert.DoesNotContain(stored.TokenHash, string.Join(",", devices));
    }

    [Fact]
    public async Task ValidationRechecksRevocationAndExpiryOnEveryCall()
    {
        var f = new Fixture();
        var revoked = await f.IssueAsync("revoked");
        var expired = await f.IssueAsync("expires");
        Assert.NotNull(await f.Service.ValidateSessionAsync(revoked.Token));
        Assert.True(await f.Service.RevokeDeviceAsync(revoked.Device.Id));
        Assert.Null(await f.Service.ValidateSessionAsync(revoked.Token));
        Assert.False(await f.Service.RevokeDeviceAsync(Guid.NewGuid()));
        var revokedInfo = Assert.Single(await f.Service.ListDevicesAsync(), x => x.Id == revoked.Device.Id);
        Assert.Equal(f.Clock.Now, revokedInfo.RevokedAt);
        f.Clock.Now = expired.Device.ExpiresAt.AddTicks(-1);
        Assert.NotNull(await f.Service.ValidateSessionAsync(expired.Token));
        f.Clock.Now = expired.Device.ExpiresAt;
        Assert.Null(await f.Service.ValidateSessionAsync(expired.Token));
    }

    [Fact]
    public async Task UnknownOrMalformedSecretsAndMissingPairingsFailClosed()
    {
        var f = new Fixture();
        foreach (var invalid in new[] { "", " ", "not-a-token", new string('a', 43), new string('a', 4096), null! })
        {
            Assert.Null(await f.Service.ValidateSessionAsync(invalid));
            Assert.Equal(DevicePairingState.Unavailable, (await f.Service.RedeemPairingAsync(invalid, invalid)).State);
        }
        Assert.False(await f.Service.ApprovePairingAsync(null!));
    }

    [Fact]
    public async Task StoreFailureCannotReuseAnAlreadyConsumedApproval()
    {
        var f = new Fixture();
        var pairing = await f.Service.BeginPairingAsync("browser");
        await f.Service.ApprovePairingAsync(pairing.ApprovalCode);
        f.Store.FailWrites = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            f.Service.RedeemPairingAsync(pairing.PairingId, pairing.PairingSecret));
        f.Store.FailWrites = false;
        Assert.Equal(DevicePairingState.Unavailable,
            (await f.Service.RedeemPairingAsync(pairing.PairingId, pairing.PairingSecret)).State);
        Assert.Empty(f.Store.Sessions);
    }

    [Fact]
    public async Task PendingPairingsDoNotSurviveServiceRestartButSessionsDo()
    {
        var f = new Fixture();
        var pairing = await f.Service.BeginPairingAsync("pending");
        var grant = await f.IssueAsync("existing");
        var restarted = new DeviceAccessService(f.Store, f.Clock);
        Assert.Equal(DevicePairingState.Unavailable,
            (await restarted.RedeemPairingAsync(pairing.PairingId, pairing.PairingSecret)).State);
        Assert.Equal(grant.Device, await restarted.ValidateSessionAsync(grant.Token));
    }

    [Fact]
    public async Task TrustedIssuerSupportsShortLifetimeAndExplicitManagementPermission()
    {
        var f = new Fixture();
        IDeviceSessionIssuer issuer = f.Service;
        var shortSession = await issuer.IssueSessionAsync("short session", TimeSpan.FromMinutes(5), DevicePermissions.ProviderAccess);
        var local = await issuer.IssueSessionAsync("local", TimeSpan.FromDays(30),
            DevicePermissions.ProviderAccess | DevicePermissions.ManageDevices);
        Assert.Equal(f.Clock.Now.AddMinutes(5), shortSession.Device.ExpiresAt);
        Assert.Equal(DevicePermissions.ProviderAccess, shortSession.Device.Permissions);
        Assert.Equal(DevicePermissions.ProviderAccess | DevicePermissions.ManageDevices,
            (await f.Service.ValidateSessionAsync(local.Token))!.Permissions);
        f.Clock.Now = shortSession.Device.ExpiresAt;
        Assert.Null(await f.Service.ValidateSessionAsync(shortSession.Token));
        Assert.NotNull(await f.Service.ValidateSessionAsync(local.Token));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-1, 1)]
    [InlineData(43201, 1)]
    [InlineData(1, 0)]
    [InlineData(1, 2)]
    [InlineData(1, 5)]
    public async Task TrustedIssuerRejectsUnsupportedLifetimeOrPermissions(int minutes, int permissions)
    {
        var f = new Fixture();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => f.Service.IssueSessionAsync("device",
            TimeSpan.FromMinutes(minutes), (DevicePermissions)permissions));
        Assert.Empty(f.Store.Sessions);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("a\nb")]
    [InlineData(null)]
    public async Task DeviceNamesMustBePrintableAndNonempty(string? name)
    {
        var f = new Fixture();
        await Assert.ThrowsAsync<ArgumentException>(() => f.Service.BeginPairingAsync(name!));
        await Assert.ThrowsAsync<ArgumentException>(() => f.IssueAsync(name!));
    }

    [Fact]
    public async Task DeviceNameLengthIsBounded()
    {
        var f = new Fixture();
        await Assert.ThrowsAsync<ArgumentException>(() => f.Service.BeginPairingAsync(new string('x', 101)));
        Assert.NotNull(await f.Service.BeginPairingAsync(new string('x', 100)));
    }

    private sealed class Fixture
    {
        public Clock Clock { get; } = new();
        public MemoryStore Store { get; } = new();
        public DeviceAccessService Service { get; }
        public Fixture() => Service = new(Store, Clock);
        public Task<DeviceSessionGrant> IssueAsync(string name) => Service.IssueSessionAsync(name,
            TimeSpan.FromDays(30), DevicePermissions.ProviderAccess);
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 17, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class MemoryStore : IDeviceSessionStore
    {
        public Dictionary<Guid, DeviceSession> Sessions { get; } = new();
        public bool FailWrites { get; set; }
        public Task CreateAsync(DeviceSession session, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (FailWrites) throw new InvalidOperationException("Simulated store failure.");
            Sessions.Add(session.Id, session);
            return Task.CompletedTask;
        }
        public Task<DeviceSession?> FindByTokenHashAsync(string hash, CancellationToken cancellationToken = default)
            => Task.FromResult(Sessions.Values.SingleOrDefault(x => x.TokenHash == hash));
        public Task<IReadOnlyList<DeviceSession>> ListAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<DeviceSession>>(Sessions.Values.ToArray());
        public Task<bool> RevokeAsync(Guid id, DateTimeOffset revokedAt, CancellationToken cancellationToken = default)
        {
            if (!Sessions.TryGetValue(id, out var session)) return Task.FromResult(false);
            session.RevokedAt ??= revokedAt;
            return Task.FromResult(true);
        }
    }
}
