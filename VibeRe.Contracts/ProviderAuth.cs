namespace VibeRe.Contracts;

// Version the public endpoint independently from application objects. JSON DTOs have no transport dependencies.
public static class ServerProtocol
{
    public const string AuthHubPath = "/hubs/v1/auth";
    public const string ReadinessPath = "/.well-known/vibe-re/server";
    public const int Version = 2;
    public const string Transport = "signalr";
}

// Numeric JSON values are part of the v1 wire contract; append rather than reorder them.
public enum AuthState { SignedOut = 0, Authenticated = 1, InvalidCredential = 2, SubscriptionRequired = 3, Unavailable = 4 }
public sealed record AuthStatusReply
{
    public AuthState State { get; init; }
    public string Message { get; init; } = "";
}

public enum BrowserLoginState { Waiting = 0, Completed = 1, Failed = 2, Expired = 3, Canceled = 4 }
public sealed record BrowserLoginReply
{
    public string AttemptId { get; init; } = "";
    public string SignInUrl { get; init; } = "";
    public long ExpiresAtUnixSeconds { get; init; }
    public BrowserLoginState State { get; init; }
    public string Message { get; init; } = "";
    public AuthStatusReply? AuthStatus { get; init; }
}
