using VibeRe.Domain.Tools;

namespace VibeRe.Application.Tools;

public interface IMcpServerRepository
{
    Task<IReadOnlyList<McpServer>> ListAsync(CancellationToken cancellationToken = default);

    Task<McpServer?> GetByNameAsync(string name, CancellationToken cancellationToken = default);

    Task AddAsync(McpServer server, CancellationToken cancellationToken = default);

    Task<bool> SetEnabledAsync(string name, bool enabled, CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(string name, CancellationToken cancellationToken = default);
}
