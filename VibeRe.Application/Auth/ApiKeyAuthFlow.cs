namespace VibeRe.Core.Auth;

/// <summary>Provider-agnostic flow where the user pastes an API key they created themselves.</summary>
public sealed class ApiKeyAuthFlow(string providerDisplayName, Uri? createKeyUrl = null) : IAuthFlow
{
    public const string FlowId = "api-key";

    public string Id => FlowId;

    public string DisplayName => "Enter an API key";

    public async Task<string> AcquireSecretAsync(IAuthInteraction interaction, CancellationToken cancellationToken = default)
    {
        var prompt = createKeyUrl is null
            ? $"{providerDisplayName} API key"
            : $"{providerDisplayName} API key (create one at {createKeyUrl})";
        var apiKey = (await interaction.PromptSecretAsync(prompt, cancellationToken))?.Trim();
        if (string.IsNullOrEmpty(apiKey))
        {
            throw new AuthException("No API key was entered.", AuthErrorCode.MissingSecret);
        }

        return apiKey;
    }
}
