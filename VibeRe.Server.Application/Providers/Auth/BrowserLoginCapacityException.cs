namespace VibeRe.Server.Application.Providers.Auth;

public sealed class BrowserLoginCapacityException() : Exception("Too many pending browser logins.");
