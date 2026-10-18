namespace VibeRe.Client.Application;

public enum AuthState { SignedOut, Authenticated, InvalidCredential, SubscriptionRequired, Unavailable }

public sealed record AuthStatus
{
    public AuthState State { get; init; }
    public string Message { get; init; } = "";
    public bool NeedsLogin => State is AuthState.SignedOut or AuthState.InvalidCredential;
}

public enum BrowserLoginState { Waiting, Completed, Failed, Expired, Canceled }

public sealed record BrowserLoginResult
{
    public string SignInUrl { get; init; } = "";
    public BrowserLoginState State { get; init; }
    public string Message { get; init; } = "";
    public AuthStatus? AuthStatus { get; init; }
}

// Transport adapters translate failures into an application error; callers need no gRPC types.
public sealed class ServerConnectionException(string message, Exception? innerException = null)
    : Exception(message, innerException);
