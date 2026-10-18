using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using VibeRe.Domain.Access;
using VibeRe.Domain.Credentials;
using VibeRe.Domain.Mcp;
using VibeRe.Domain.Provider;
using VibeRe.Server.Application.Access;
using VibeRe.Server.Infrastructure.Persistence;

namespace VibeRe.Server.Infrastructure.Tests;

public sealed class DeviceSessionStoreTests
{
    [Fact]
    public async Task IssuedSessionPersistsOnlyHashAndSurvivesServiceAndContextRestart()
    {
        using var fixture = new Fixture();
        await fixture.MigrateAsync();
        var service = fixture.CreateService();
        var grant = await service.IssueSessionAsync("tablet", TimeSpan.FromDays(30), DevicePermissions.ProviderAccess);
        await using (var db = fixture.Factory.CreateDbContext())
        {
            var entity = Assert.Single(await db.DeviceSessions.AsNoTracking().ToArrayAsync());
            Assert.Equal(grant.Device.Id, entity.Id);
            Assert.Equal(Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(grant.Token))), entity.TokenHash);
            Assert.Equal(DevicePermissions.ProviderAccess, entity.Permissions);
            Assert.Equal(grant.Device.CreatedAt, entity.CreatedAt);
            Assert.Equal(grant.Device.ExpiresAt, entity.ExpiresAt);
        }
        var restarted = fixture.CreateService();
        Assert.Equal(grant.Device, await restarted.ValidateSessionAsync(grant.Token));
        Assert.Equal(grant.Device, Assert.Single(await restarted.ListDevicesAsync()));
        var databaseBytes = await File.ReadAllBytesAsync(fixture.DatabasePath);
        Assert.Equal(-1, databaseBytes.AsSpan().IndexOf(Encoding.ASCII.GetBytes(grant.Token)));
    }

    [Fact]
    public async Task RevocationPersistsImmediatelyAcrossServicesAndIsIdempotent()
    {
        using var fixture = new Fixture();
        await fixture.MigrateAsync();
        var first = fixture.CreateService();
        var second = fixture.CreateService();
        var grant = await first.IssueSessionAsync("desktop", TimeSpan.FromDays(30),
            DevicePermissions.ProviderAccess | DevicePermissions.ManageDevices);
        Assert.NotNull(await second.ValidateSessionAsync(grant.Token));
        var revokedAt = fixture.Clock.Now;
        Assert.True(await first.RevokeDeviceAsync(grant.Device.Id));
        Assert.Null(await second.ValidateSessionAsync(grant.Token));
        fixture.Clock.Now = fixture.Clock.Now.AddMinutes(1);
        Assert.True(await second.RevokeDeviceAsync(grant.Device.Id));
        var info = Assert.Single(await first.ListDevicesAsync());
        Assert.Equal(revokedAt, info.RevokedAt);
        Assert.Equal(DevicePermissions.ProviderAccess | DevicePermissions.ManageDevices, info.Permissions);
        Assert.False(await first.RevokeDeviceAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task ReopenedDatabaseStillEnforcesServerExpiry()
    {
        using var fixture = new Fixture();
        await fixture.MigrateAsync();
        var service = fixture.CreateService();
        var grant = await service.IssueSessionAsync("short session", TimeSpan.FromMinutes(5), DevicePermissions.ProviderAccess);
        fixture.Clock.Now = grant.Device.ExpiresAt;
        Assert.Null(await fixture.CreateService().ValidateSessionAsync(grant.Token));
        Assert.Single(await service.ListDevicesAsync());
    }

    [Fact]
    public async Task TokenHashHasUniqueIndexAndUnknownHashesReturnNoSession()
    {
        using var fixture = new Fixture();
        await fixture.MigrateAsync();
        var store = new DeviceSessionStore(fixture.Factory);
        Assert.Null(await store.FindByTokenHashAsync(new string('0', 64)));
        var session = new DeviceSession
        {
            Id = Guid.NewGuid(), Name = "first", TokenHash = new string('A', 64),
            Permissions = DevicePermissions.ProviderAccess,
            CreatedAt = fixture.Clock.Now, ExpiresAt = fixture.Clock.Now.AddDays(30)
        };
        await store.CreateAsync(session);
        await Assert.ThrowsAsync<DbUpdateException>(() => store.CreateAsync(new DeviceSession
        {
            Id = Guid.NewGuid(), Name = "duplicate", TokenHash = session.TokenHash,
            Permissions = DevicePermissions.ProviderAccess,
            CreatedAt = fixture.Clock.Now, ExpiresAt = fixture.Clock.Now.AddDays(30)
        }));
        Assert.Single(await store.ListAsync());
    }

    [Fact]
    public async Task MigrationPreservesExistingProfilesCredentialsAndMcpConnections()
    {
        using var fixture = new Fixture();
        var credentialId = Guid.NewGuid();
        var profileId = Guid.NewGuid();
        var mcpId = Guid.NewGuid();
        await using (var db = fixture.Factory.CreateDbContext())
        {
            await db.GetService<IMigrator>().MigrateAsync("20260929135733_Initial");
            var credential = new Credential { Id = credentialId, Kind = CredentialKind.ApiKey };
            db.ProviderProfiles.Add(new ProviderProfile
            {
                Id = profileId, Name = "existing profile", ProviderIdentifier = "mistral",
                BaseUrl = "https://api.mistral.ai", Credential = credential,
                CreatedAt = fixture.Clock.Now, UpdatedAt = fixture.Clock.Now
            });
            db.McpConnections.Add(new McpConnection { Id = mcpId, Credential = credential });
            await db.SaveChangesAsync();
        }
        await fixture.MigrateAsync();
        await using var upgraded = fixture.Factory.CreateDbContext();
        Assert.Equal(2, (await upgraded.Database.GetAppliedMigrationsAsync()).Count());
        Assert.Empty(await upgraded.Database.GetPendingMigrationsAsync());
        Assert.False(upgraded.Database.HasPendingModelChanges());
        var profile = Assert.Single(await upgraded.ProviderProfiles.Include(x => x.Credential).ToArrayAsync());
        Assert.Equal(profileId, profile.Id);
        Assert.Equal("existing profile", profile.Name);
        Assert.Equal("mistral", profile.ProviderIdentifier);
        Assert.Equal("https://api.mistral.ai", profile.BaseUrl);
        Assert.Equal(credentialId, profile.Credential!.Id);
        Assert.Equal(CredentialKind.ApiKey, Assert.Single(await upgraded.Credentials.ToArrayAsync()).Kind);
        Assert.Equal(mcpId, Assert.Single(await upgraded.McpConnections.ToArrayAsync()).Id);
        Assert.Empty(await upgraded.DeviceSessions.ToArrayAsync());
        var grant = await fixture.CreateService().IssueSessionAsync("new client", TimeSpan.FromDays(30), DevicePermissions.ProviderAccess);
        Assert.NotNull(await fixture.CreateService().ValidateSessionAsync(grant.Token));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "vibe-device-tests-" + Guid.NewGuid().ToString("N"));
        public string DatabasePath { get; }
        public Factory Factory { get; }
        public Clock Clock { get; } = new();
        public Fixture()
        {
            Directory.CreateDirectory(_directory);
            DatabasePath = Path.Combine(_directory, "server.db");
            Factory = new(new DbContextOptionsBuilder<VibeReDbContext>()
                .UseSqlite($"Data Source={DatabasePath};Pooling=False").Options);
        }
        public async Task MigrateAsync()
        {
            await using var db = Factory.CreateDbContext();
            await db.Database.MigrateAsync();
        }
        public DeviceAccessService CreateService() => new(new DeviceSessionStore(Factory), Clock);
        public void Dispose() => Directory.Delete(_directory, true);
    }

    private sealed class Factory(DbContextOptions<VibeReDbContext> options) : IDbContextFactory<VibeReDbContext>
    {
        public VibeReDbContext CreateDbContext() => new(options);
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 17, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
