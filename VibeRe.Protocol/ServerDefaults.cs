namespace VibeRe.Protocol;

/// <summary>Where a local VibeRe server listens unless configured otherwise.</summary>
public static class ServerDefaults
{
    public const int Port = 5067;

    public static Uri Address { get; } = new($"http://localhost:{Port}");
}
