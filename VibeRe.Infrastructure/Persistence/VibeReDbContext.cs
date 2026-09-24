using Microsoft.EntityFrameworkCore;
using VibeRe.Domain.Conversations;
using VibeRe.Domain.Tools;

namespace VibeRe.Infrastructure.Persistence;

public sealed class VibeReDbContext(DbContextOptions<VibeReDbContext> options) : DbContext(options)
{
    public DbSet<Conversation> Conversations => Set<Conversation>();

    public DbSet<ConversationEvent> ConversationEvents => Set<ConversationEvent>();

    public DbSet<ContextNode> ContextNodes => Set<ContextNode>();

    public DbSet<ContextBlob> ContextBlobs => Set<ContextBlob>();

    public DbSet<ContextBranch> ContextBranches => Set<ContextBranch>();

    public DbSet<McpServer> McpServers => Set<McpServer>();

    public DbSet<ToolPolicy> ToolPolicies => Set<ToolPolicy>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(VibeReDbContext).Assembly);
    }
}
