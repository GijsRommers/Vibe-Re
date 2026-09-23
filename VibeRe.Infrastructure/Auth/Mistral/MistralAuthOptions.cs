namespace VibeRe.Infrastructure.Auth.Mistral;

public sealed record MistralAuthOptions
{
    /// <summary>Origin of the Mistral console; sign-in pages must live under it.</summary>
    public string ConsoleBaseUrl { get; init; } = "https://console.mistral.ai";

    /// <summary>Base of the console API hosting the <c>/vibe/sign-in</c> and <c>/vibe/whoami</c> endpoints.</summary>
    public string ConsoleApiBaseUrl { get; init; } = "https://console.mistral.ai/api";

    /// <summary>Where users can create an API key by hand.</summary>
    public string CreateApiKeyUrl { get; init; } = "https://chat.mistral.ai/code/extensions?focus=key";

    /// <summary>
    /// Split-horizon deployments reach the console on a host the server doesn't know about, so it returns
    /// sign-in/poll URLs on its own public host. When set, those URLs are re-homed onto the configured base
    /// URLs instead of being rejected for an origin mismatch.
    /// </summary>
    public bool AllowOriginRewrite { get; init; }

    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(3);

    public int MaxConsecutivePollFailures { get; init; } = 3;
}
