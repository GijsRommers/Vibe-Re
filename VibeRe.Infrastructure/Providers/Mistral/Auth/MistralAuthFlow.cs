using VibeRe.Application.Providers.Auth;

namespace VibeRe.Infrastructure.Providers.Mistral.Auth;

public class MistralAuthFlow: IAuthFlow
{
    public string ProviderName { get; }
}