namespace VibeRe.Core.Auth;

/// <summary>A secret (typically an API key) that authenticates against a model provider.</summary>
public sealed record Credential(string ProviderId, string Secret)
{
    // Keep secrets out of logs and debugger output.
    public override string ToString() => $"Credential {{ ProviderId = {ProviderId}, Secret = *** }}";
}

/// <summary>Where the active credential for a provider comes from.</summary>
public enum CredentialSource
{
    None,
    Environment,
    Store,
}

public sealed record AuthState(
    string ProviderId,
    CredentialSource ActiveSource,
    bool HasStoredCredential,
    string? EnvironmentVariable)
{
    public bool IsSignedIn => ActiveSource != CredentialSource.None;
}

/// <summary>Account details a provider reports for a credential.</summary>
public sealed record AccountInfo(string Plan);
