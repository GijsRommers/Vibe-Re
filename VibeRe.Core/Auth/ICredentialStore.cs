namespace VibeRe.Core.Auth;

/// <summary>Persists credentials between runs without exposing how they are stored.</summary>
public interface ICredentialStore
{
    Task<Credential?> GetAsync(string providerId, CancellationToken cancellationToken = default);

    Task SetAsync(Credential credential, CancellationToken cancellationToken = default);

    /// <summary>Removes the stored credential. Returns <c>false</c> if there was none.</summary>
    Task<bool> DeleteAsync(string providerId, CancellationToken cancellationToken = default);
}
