using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using VibeRe.Domain.Conversations;

namespace VibeRe.Infrastructure.Persistence.Configurations;

public sealed class ConversationEventConfiguration : IEntityTypeConfiguration<ConversationEvent>
{
    public void Configure(EntityTypeBuilder<ConversationEvent> builder)
    {
        builder.ToTable("conversation_events");
        builder.HasKey(entry => entry.Id);
        builder.Property(entry => entry.Id).ValueGeneratedNever();
        builder.Property(entry => entry.Kind).IsRequired();
        builder.Property(entry => entry.DataJson).IsRequired();
        builder.HasIndex(entry => new { entry.ConversationId, entry.Sequence }).IsUnique();
        builder.HasIndex(entry => new { entry.ConversationId, entry.TurnId, entry.Sequence });
        builder.HasOne<Conversation>()
            .WithMany()
            .HasForeignKey(entry => entry.ConversationId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
