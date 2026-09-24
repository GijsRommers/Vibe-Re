using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using VibeRe.Domain.Tools;

namespace VibeRe.Infrastructure.Persistence.Configurations;

public sealed class ToolPolicyConfiguration : IEntityTypeConfiguration<ToolPolicy>
{
    public void Configure(EntityTypeBuilder<ToolPolicy> builder)
    {
        builder.ToTable("tool_policies");
        builder.HasKey(policy => policy.ToolName);
        builder.Property(policy => policy.Approval).HasConversion<string>();
    }
}
