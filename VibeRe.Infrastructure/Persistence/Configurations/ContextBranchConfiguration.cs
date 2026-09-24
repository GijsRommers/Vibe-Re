using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using VibeRe.Domain.Conversations;

namespace VibeRe.Infrastructure.Persistence.Configurations;

public sealed class ContextBranchConfiguration : IEntityTypeConfiguration<ContextBranch>
{
    public void Configure(EntityTypeBuilder<ContextBranch> builder)
    {
        builder.ToTable("context_branches");
        builder.HasKey(branch => branch.Id);
        builder.Property(branch => branch.Id).ValueGeneratedNever();
        builder.Property(branch => branch.Name).IsRequired();
        builder.HasIndex(branch => new { branch.ConversationId, branch.Name }).IsUnique();
        builder.HasOne<Conversation>()
            .WithMany()
            .HasForeignKey(branch => branch.ConversationId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<ContextNode>()
            .WithMany()
            .HasForeignKey(branch => branch.HeadNodeId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<ContextNode>()
            .WithMany()
            .HasForeignKey(branch => branch.ForkedFromNodeId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
