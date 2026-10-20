using Microsoft.EntityFrameworkCore;
using VibeRe.Domain.Access;
using VibeRe.Domain.Chat;
using VibeRe.Domain.Credentials;
using VibeRe.Domain.Mcp;
using VibeRe.Domain.Provider;

namespace VibeRe.Server.Infrastructure.Persistence;

public sealed class VibeReDbContext(DbContextOptions<VibeReDbContext> options) : DbContext(options)
{
    public DbSet<WorkspaceThread> WorkspaceThreads => Set<WorkspaceThread>();
    public DbSet<WorkspaceMessage> WorkspaceMessages => Set<WorkspaceMessage>();
    public DbSet<DeviceSession> DeviceSessions => Set<DeviceSession>();
    public DbSet<ProviderProfile> ProviderProfiles => Set<ProviderProfile>();
    public DbSet<Credential> Credentials => Set<Credential>();
    public DbSet<McpConnection> McpConnections => Set<McpConnection>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.Entity<WorkspaceThread>(entity =>
        {
            entity.Property(x => x.Title).HasMaxLength(160).IsRequired();
            entity.HasMany(x => x.Messages).WithOne().HasForeignKey(x => x.ThreadId).OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<WorkspaceMessage>().HasIndex(x => new { x.ThreadId, x.Sequence }).IsUnique();
        modelBuilder.Entity<DeviceSession>(entity =>
        {
            entity.Property(x => x.Name).HasMaxLength(100).IsRequired();
            entity.Property(x => x.TokenHash).HasMaxLength(64).IsRequired();
            entity.HasIndex(x => x.TokenHash).IsUnique();
        });
    }
}
