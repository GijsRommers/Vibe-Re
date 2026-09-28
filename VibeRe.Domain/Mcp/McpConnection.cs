using VibeRe.Domain.Common;

namespace VibeRe.Domain.Mcp;

public class McpConnection: TimeStampedEntity
{
    public Guid Id { get; set; }

    public McpTransportType TransportType { get; set; }

    public McpAuthenticationType AuthenticationType { get; set; }
}