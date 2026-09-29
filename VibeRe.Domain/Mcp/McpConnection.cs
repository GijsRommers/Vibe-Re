using VibeRe.Domain.Common;
using VibeRe.Domain.Credentials;

namespace VibeRe.Domain.Mcp;

public class McpConnection : TimeStampedEntity
{
    public Guid Id { get; set; }

    public McpTransportType TransportType { get; set; }

    public Credential? Credential { get; set; }

}