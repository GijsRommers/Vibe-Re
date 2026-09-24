using Microsoft.Extensions.DependencyInjection;
using VibeRe.Application.Tools;
using VibeRe.Infrastructure.Tools.Mcp;

namespace VibeRe.Infrastructure.Tools;

public static class ToolServiceCollectionExtensions
{
    /// <summary>
    /// Registers the <see cref="ToolRegistry"/> with MCP servers as a tool source. Built-in tools join by
    /// registering more <see cref="IToolSource"/>s. Requires <c>AddVibeRePersistence</c>.
    /// </summary>
    public static IServiceCollection AddVibeReTools(this IServiceCollection services)
    {
        services.AddSingleton<McpClientPool>();
        services.AddScoped<IToolSource, McpToolSource>();
        services.AddScoped<ToolRegistry>();
        return services;
    }
}
