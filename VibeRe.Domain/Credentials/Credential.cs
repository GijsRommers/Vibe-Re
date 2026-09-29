using VibeRe.Domain.Common;

namespace VibeRe.Domain.Credentials;

public class Credential : TimeStampedEntity
{
    public Guid Id { get; set; }

    public CredentialKind Kind { get; set; }
}