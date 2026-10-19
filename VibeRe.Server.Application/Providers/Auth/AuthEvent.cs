namespace VibeRe.Server.Application.Providers.Auth;

public sealed record AuthEvent(
    AuthEventKind Kind,
    Guid? ProviderProfileId = null,
    string? Message = null);
    