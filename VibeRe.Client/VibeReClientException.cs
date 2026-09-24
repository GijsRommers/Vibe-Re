namespace VibeRe.Client;

/// <summary>The server rejected a request; <see cref="Exception.Message"/> is safe to show the user.</summary>
public class VibeReClientException(string message, Exception? innerException = null)
    : Exception(message, innerException);

public sealed class ServerUnavailableException(Uri address, string? message = null, Exception? innerException = null)
    : VibeReClientException(message ?? $"Couldn't reach the VibeRe server at {address}. Is it running?", innerException)
{
    public Uri Address { get; } = address;
}
