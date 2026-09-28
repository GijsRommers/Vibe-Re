namespace VibeRe.Domain.Common;

public interface ITimestampedEntity
{
    DateTimeOffset CreatedAt { get; set; }
    DateTimeOffset UpdatedAt { get; set; }
}