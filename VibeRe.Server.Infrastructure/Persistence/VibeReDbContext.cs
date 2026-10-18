using Microsoft.EntityFrameworkCore;
using VibeRe.Domain.Access;
using VibeRe.Domain.Credentials;
using VibeRe.Domain.Mcp;
using VibeRe.Domain.Provider;

namespace VibeRe.Server.Infrastructure.Persistence;

public sealed class VibeReDbContext(DbContextOptions<VibeReDbContext> options) : DbContext(options)
{
    public DbSet<DeviceSession> DeviceSessions => Set<DeviceSession>();
    public DbSet<ProviderProfile> ProviderProfiles => Set<ProviderProfile>();
    public DbSet<Credential> Credentials => Set<Credential>();
    public DbSet<McpConnection> McpConnections => Set<McpConnection>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.Entity<DeviceSession>(entity =>
        {
            entity.Property(x => x.Name).HasMaxLength(100).IsRequired();
            entity.Property(x => x.TokenHash).HasMaxLength(64).IsRequired();
            entity.HasIndex(x => x.TokenHash).IsUnique();
        });
    }
}
