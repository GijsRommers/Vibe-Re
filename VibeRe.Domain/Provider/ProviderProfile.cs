using VibeRe.Domain.Common;
using VibeRe.Domain.Credentials;

namespace VibeRe.Domain.Provider;

public class ProviderProfile : TimeStampedEntity
{
    public Guid Id { get; set; }

    public string ProviderIdentifier { get; set; }

    public Credential? Credential { get; set; }

    public string BaseUrl { get; set; }

    public string Name { get; set; }
}