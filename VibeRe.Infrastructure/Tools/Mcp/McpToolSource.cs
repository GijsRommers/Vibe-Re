using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using VibeRe.Application.Tools;

namespace VibeRe.Infrastructure.Tools.Mcp;

/// <summary>
/// Exposes the tools of every enabled MCP server in <see cref="IMcpServerRepository"/>. A server that fails
/// to start or answer is skipped (and logged) so one broken server doesn't take every tool down.
/// </summary>
public sealed class McpToolSource(
    IMcpServerRepository servers,
    McpClientPool pool,
    ILogger<McpToolSource>? logger = null) : IToolSource
{
    public async Task<IReadOnlyList<ITool>> GetToolsAsync(CancellationToken cancellationToken = default)
    {
        var enabled = (await servers.ListAsync(cancellationToken)).Where(server => server.Enabled).ToList();
        pool.RetainOnly(enabled.Select(server => server.Id));

        var perServer = await Task.WhenAll(enabled.Select(async server =>
        {
            try
            {
                var client = await pool.GetClientAsync(server, cancellationToken);
                var tools = await client.ListToolsAsync(cancellationToken: cancellationToken);
                return tools.Select(tool => (ITool)new McpTool(server.Name, tool)).ToList();
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                logger?.LogWarning(exception, "Skipping MCP server '{Server}': it could not be reached.", server.Name);
                return [];
            }
        }));

        return perServer.SelectMany(tools => tools).ToList();
    }
}
