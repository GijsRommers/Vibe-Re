using Microsoft.Extensions.DependencyInjection;
using VibeRe.Application.Tools;
using VibeRe.Tools.Search;

namespace VibeRe.Tools;

public static class ToolsServiceCollectionExtensions
{
    /// <summary>
    /// Registers the <see cref="ToolRegistry"/> with the built-in tools. Other sources (e.g. <c>AddVibeReMcp</c>)
    /// add themselves as <see cref="IToolSource"/>s. Requires <c>AddVibeRePersistence</c> for tool policies.
    /// </summary>
    public static IServiceCollection AddVibeReTools(this IServiceCollection services)
    {
        services.AddSingleton(_ => new Ripgrep());
        services.AddSingleton<IToolSource, BuiltinToolSource>();
        services.AddScoped<ToolRegistry>();
        return services;
    }
}
