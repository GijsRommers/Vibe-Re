namespace VibeRe.Server.Application.Providers.Auth;

public enum ProviderAuthState
{
    SignedOut,
    Authenticated,
    InvalidCredential,
    SubscriptionRequired,
    Unavailable
}

public sealed record ProviderAuthStatus(ProviderAuthState State, string Message);
