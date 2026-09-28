namespace VibeRe.Domain.Common;

public abstract class TimeStampedEntity: ITimestampedEntity
{
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    
}