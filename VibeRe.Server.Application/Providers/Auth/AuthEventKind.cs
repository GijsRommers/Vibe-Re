namespace VibeRe.Server.Application.Providers.Auth;

public enum AuthEventKind
{
    WaitingForBrowser,
    Exchanging,
    Completed,
    Failed
}