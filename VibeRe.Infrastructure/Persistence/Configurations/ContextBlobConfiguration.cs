using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using VibeRe.Domain.Conversations;

namespace VibeRe.Infrastructure.Persistence.Configurations;

public sealed class ContextBlobConfiguration : IEntityTypeConfiguration<ContextBlob>
{
    public void Configure(EntityTypeBuilder<ContextBlob> builder)
    {
        builder.ToTable("context_blobs");
        builder.HasKey(blob => blob.Hash);
        builder.Property(blob => blob.Bytes).IsRequired();
    }
}
