using Microsoft.Extensions.DependencyInjection;
using VibeRe.Application.Tools;

namespace VibeRe.Infrastructure.Mcp;

public static class McpServiceCollectionExtensions
{
    /// <summary>
    /// Adds the tools of the MCP servers stored in <see cref="IMcpServerRepository"/> as an
    /// <see cref="IToolSource"/>. Requires <c>AddVibeRePersistence</c>.
    /// </summary>
    public static IServiceCollection AddVibeReMcp(this IServiceCollection services)
    {
        services.AddSingleton<McpClientPool>();
        services.AddScoped<IToolSource, McpToolSource>();
        return services;
    }
}
