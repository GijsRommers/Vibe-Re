using VibeRe.Application.Auth;

namespace VibeRe.Application.Tests.Auth;

internal sealed class RecordingInteraction(string? secret = null) : IAuthInteraction
{
    public void ReportStatus(AuthStatus status)
    {
    }

    public void OpenSignInUrl(Uri url, DateTimeOffset expiresAt)
    {
    }

    public Task<string?> PromptSecretAsync(string prompt, CancellationToken cancellationToken) => Task.FromResult(secret);
}
