using Microsoft.EntityFrameworkCore;
using VibeRe.Domain.Credentials;
using VibeRe.Domain.Mcp;
using VibeRe.Domain.Provider;

namespace VibeRe.Infrastructure.Persistence;

public sealed class VibeReDbContext(DbContextOptions<VibeReDbContext> options) : DbContext(options)
{
    public DbSet<ProviderProfile> ProviderProfiles => Set<ProviderProfile>();
    public DbSet<Credential> Credentials => Set<Credential>();
    public DbSet<McpConnection> McpConnections => Set<McpConnection>();
}