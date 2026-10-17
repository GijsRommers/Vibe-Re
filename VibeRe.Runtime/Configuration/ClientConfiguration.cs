namespace VibeRe.Runtime.Configuration;

public sealed record ClientConfiguration(string? ActiveServer, List<ServerConnection> Servers, bool ServerRequired = true)
{
    public static ClientConfiguration Default() => new("local",
        [new(Guid.NewGuid(), "local", "http://localhost:5067", StartLocally: !OperatingSystem.IsAndroid() && !OperatingSystem.IsIOS())]);

    public ServerConnection Find(string? name = null) =>
        Servers.FirstOrDefault(x => string.Equals(x.Name, name ?? ActiveServer, StringComparison.OrdinalIgnoreCase))
        ?? throw new ArgumentException("Select a configured server first.");
}
