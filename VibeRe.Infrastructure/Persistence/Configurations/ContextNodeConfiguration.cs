using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using VibeRe.Domain.Conversations;

namespace VibeRe.Infrastructure.Persistence.Configurations;

public sealed class ContextNodeConfiguration : IEntityTypeConfiguration<ContextNode>
{
    public void Configure(EntityTypeBuilder<ContextNode> builder)
    {
        builder.ToTable("context_nodes");
        builder.HasKey(node => node.Id);
        builder.Property(node => node.Id).ValueGeneratedNever();
        builder.Property(node => node.Role).IsRequired();
        builder.Property(node => node.PayloadHash).IsRequired();
        builder.Property(node => node.PrefixHash).IsRequired();
        builder.HasIndex(node => node.ParentId);
        builder.HasIndex(node => new { node.ConversationId, node.TurnId });
        builder.HasIndex(node => node.PrefixHash);
        builder.HasOne<Conversation>()
            .WithMany()
            .HasForeignKey(node => node.ConversationId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<ContextNode>()
            .WithMany()
            .HasForeignKey(node => node.ParentId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<ContextBlob>()
            .WithMany()
            .HasForeignKey(node => node.PayloadHash)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
