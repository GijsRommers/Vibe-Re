using VibeRe.Domain.Common;
using VibeRe.Domain.Credentials;

namespace VibeRe.Domain.Provider;

public class ProviderProfile: TimeStampedEntity
{
    public Guid Id { get; set; }

    public Credential Credential { get; set; }

    public String BaseUrl { get; set; }

    public String Name { get; set; }
}