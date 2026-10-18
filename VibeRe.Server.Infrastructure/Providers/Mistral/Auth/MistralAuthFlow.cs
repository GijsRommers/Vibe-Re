using VibeRe.Server.Application.Providers.Auth;

namespace VibeRe.Server.Infrastructure.Providers.Mistral.Auth;

public class MistralAuthFlow : IAuthFlow
{
    public string ProviderName { get; }
}