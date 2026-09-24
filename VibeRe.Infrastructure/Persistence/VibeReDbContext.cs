using Microsoft.EntityFrameworkCore;
using VibeRe.Domain.Conversations;

namespace VibeRe.Infrastructure.Persistence;

public sealed class VibeReDbContext(DbContextOptions<VibeReDbContext> options) : DbContext(options)
{
    public DbSet<Conversation> Conversations => Set<Conversation>();

    public DbSet<ConversationEvent> ConversationEvents => Set<ConversationEvent>();

    public DbSet<ContextNode> ContextNodes => Set<ContextNode>();

    public DbSet<ContextBlob> ContextBlobs => Set<ContextBlob>();

    public DbSet<ContextBranch> ContextBranches => Set<ContextBranch>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(VibeReDbContext).Assembly);
    }
}
