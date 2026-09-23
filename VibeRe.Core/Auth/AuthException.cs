namespace VibeRe.Core.Auth;

public enum AuthErrorCode
{
    StartFailed,
    PollFailed,
    UnknownState,
    ExchangeFailed,
    MissingSecret,
    Expired,
    Denied,
    ProviderError,
    TimedOut,
    Unauthorized,
    StoreFailed,
}

public sealed class AuthException(string message, AuthErrorCode code, Exception? innerException = null)
    : Exception(message, innerException)
{
    public AuthErrorCode Code { get; } = code;
}
