namespace VibeRe.Client;

public sealed record ProviderInfo(string Id, string DisplayName, string? ApiKeyEnvironmentVariable);

/// <summary>Where the server's active credential for a provider comes from.</summary>
public enum CredentialSource
{
    None,
    Environment,
    Store,
}

public enum AccountStatus
{
    SignedOut,
    Active,
    Rejected,
    SubscriptionRequired,
}

/// <param name="Plan">The account plan, when the provider reported one.</param>
/// <param name="Message">Explains a rejected or restricted credential.</param>
public sealed record AuthStatus(
    ProviderInfo Provider,
    CredentialSource ActiveSource,
    AccountStatus AccountStatus,
    string? Plan,
    string? Message);

public sealed record SignInResult(ProviderInfo Provider, CredentialSource ActiveSource);

/// <param name="Removed"><c>false</c> when there was no stored credential.</param>
public sealed record SignOutResult(ProviderInfo Provider, bool Removed, CredentialSource ActiveSource);

public enum SignInStage
{
    OpeningBrowser,
    WaitingForUser,
    Exchanging,
    Completed,
}

/// <summary>The front end (CLI, desktop, ...) that a server-side sign-in talks to while it runs.</summary>
public interface ISignInInteraction
{
    void ReportStage(SignInStage stage);

    /// <summary>
    /// Sends the user to <paramref name="url"/> to finish signing in. Implementations should both try to
    /// open a browser and display the URL, so users without a local browser can open it themselves.
    /// </summary>
    void OpenSignInUrl(Uri url, DateTimeOffset expiresAt);

    /// <summary>Asks the user for a secret without echoing it. Returns <c>null</c> if they declined.</summary>
    Task<string?> PromptSecretAsync(string prompt, CancellationToken cancellationToken);
}
