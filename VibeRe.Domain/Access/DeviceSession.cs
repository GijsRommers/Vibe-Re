namespace VibeRe.Domain.Access;

// A device has one bounded server session. Raw bearer tokens never belong in this entity.
public sealed class DeviceSession
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string TokenHash { get; set; } = string.Empty;
    public DevicePermissions Permissions { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
}
