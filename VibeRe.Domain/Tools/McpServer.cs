using System.Text.RegularExpressions;

namespace VibeRe.Domain.Tools;

public enum McpTransport
{
    Stdio,
    Http,
}

/// <summary>
/// A user-configured MCP server. Its tools are exposed to models as <c>mcp__{Name}__{tool}</c>.
/// </summary>
public sealed partial class McpServer
{
    private McpServer() { }

    private McpServer(string name, McpTransport transport)
    {
        if (!NamePattern().IsMatch(name) || name.Contains("__", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Server names are 1-32 lowercase letters, digits, '-' or '_', without '__'.", nameof(name));
        }

        Name = name;
        Transport = transport;
    }

    /// <summary>A server launched as a child process that speaks MCP over stdin/stdout.</summary>
    public static McpServer Stdio(
        string name,
        string command,
        IEnumerable<string>? arguments = null,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        return new McpServer(name, McpTransport.Stdio)
        {
            Command = command,
            Arguments = arguments?.ToList() ?? [],
            Environment = environment?.ToDictionary() ?? [],
        };
    }

    /// <summary>A remote server reached over Streamable HTTP (falling back to SSE).</summary>
    public static McpServer Http(string name, Uri url, IReadOnlyDictionary<string, string>? headers = null)
    {
        ArgumentNullException.ThrowIfNull(url);
        if (!url.IsAbsoluteUri || url.Scheme is not ("http" or "https"))
        {
            throw new ArgumentException("An absolute http(s) URL is required.", nameof(url));
        }

        return new McpServer(name, McpTransport.Http)
        {
            Url = url.ToString(),
            Headers = headers?.ToDictionary() ?? [],
        };
    }

    public Guid Id { get; private set; } = Guid.NewGuid();

    public string Name { get; private set; } = null!;

    public McpTransport Transport { get; private set; }

    public bool Enabled { get; private set; } = true;

    public string? Command { get; private set; }

    public List<string> Arguments { get; private set; } = [];

    public Dictionary<string, string> Environment { get; private set; } = [];

    public string? Url { get; private set; }

    public Dictionary<string, string> Headers { get; private set; } = [];

    public void Enable() => Enabled = true;

    public void Disable() => Enabled = false;

    [GeneratedRegex("^[a-z0-9][a-z0-9_-]{0,31}$")]
    private static partial Regex NamePattern();
}
