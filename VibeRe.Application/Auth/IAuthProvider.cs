namespace VibeRe.Application.Auth;

/// <summary>A model vendor that VibeRe can authenticate against.</summary>
public interface IAuthProvider
{
    /// <summary>Stable identifier, used as the credential storage key (e.g. <c>mistral</c>).</summary>
    string Id { get; }

    string DisplayName { get; }

    /// <summary>Environment variable that, when set, takes precedence over any stored credential.</summary>
    string? ApiKeyEnvironmentVariable { get; }

    /// <summary>Supported sign-in flows, most preferred first.</summary>
    IReadOnlyList<IAuthFlow> Flows { get; }

    /// <summary>
    /// Looks up account details for <paramref name="credential"/>. Returns <c>null</c> when the provider
    /// has no account endpoint or it is unreachable; throws <see cref="AuthException"/> with
    /// <see cref="AuthErrorCode.Unauthorized"/> when the credential is rejected.
    /// </summary>
    Task<AccountInfo?> GetAccountAsync(Credential credential, CancellationToken cancellationToken = default);
}

/// <summary>One interactive way of obtaining a secret from a provider.</summary>
public interface IAuthFlow
{
    /// <summary>Identifier used to pick the flow explicitly (e.g. <c>browser</c>, <c>api-key</c>).</summary>
    string Id { get; }

    string DisplayName { get; }

    Task<string> AcquireSecretAsync(IAuthInteraction interaction, CancellationToken cancellationToken = default);
}

public enum AuthStatus
{
    OpeningBrowser,
    WaitingForUser,
    Exchanging,
    Completed,
}

/// <summary>The front end (CLI, desktop, ...) that an <see cref="IAuthFlow"/> talks to while it runs.</summary>
public interface IAuthInteraction
{
    void ReportStatus(AuthStatus status);

    /// <summary>
    /// Sends the user to <paramref name="url"/> to finish signing in. Implementations should both try to
    /// open a browser and display the URL, so users without a local browser can open it themselves.
    /// </summary>
    void OpenSignInUrl(Uri url, DateTimeOffset expiresAt);

    /// <summary>Asks the user for a secret without echoing it. Returns <c>null</c> if they declined.</summary>
    Task<string?> PromptSecretAsync(string prompt, CancellationToken cancellationToken);
}
