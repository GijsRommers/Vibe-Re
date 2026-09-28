namespace VibeRe.Domain.Mcp;

public class McpConnection
{
    public Guid Id { get; set; }

    public McpTransportType TransportType { get; set; }

    public McpAuthenticationType AuthenticationType { get; set; }
}