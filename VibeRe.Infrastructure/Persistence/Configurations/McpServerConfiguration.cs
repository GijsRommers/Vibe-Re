using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using VibeRe.Domain.Tools;

namespace VibeRe.Infrastructure.Persistence.Configurations;

public sealed class McpServerConfiguration : IEntityTypeConfiguration<McpServer>
{
    public void Configure(EntityTypeBuilder<McpServer> builder)
    {
        builder.ToTable("mcp_servers");
        builder.HasKey(server => server.Id);
        builder.Property(server => server.Id).ValueGeneratedNever();
        builder.Property(server => server.Name).IsRequired();
        builder.HasIndex(server => server.Name).IsUnique();
        builder.Property(server => server.Transport).HasConversion<string>();
        builder.Property(server => server.Arguments);
        builder.Property(server => server.Environment).HasConversion(DictionaryAsJson, DictionaryComparer);
        builder.Property(server => server.Headers).HasConversion(DictionaryAsJson, DictionaryComparer);
    }

    private static readonly ValueConverter<Dictionary<string, string>, string> DictionaryAsJson = new(
        value => JsonSerializer.Serialize(value, JsonSerializerOptions.Default),
        json => JsonSerializer.Deserialize<Dictionary<string, string>>(json, JsonSerializerOptions.Default) ?? new());

    private static readonly ValueComparer<Dictionary<string, string>> DictionaryComparer = new(
        (left, right) => left!.Count == right!.Count && !left.Except(right).Any(),
        value => value.Aggregate(0, (hash, pair) => hash ^ HashCode.Combine(pair.Key, pair.Value)),
        value => value.ToDictionary());
}
