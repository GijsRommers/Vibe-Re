using Microsoft.Extensions.DependencyInjection;
using VibeRe.Application.Projects;
using VibeRe.Application.Tools;

namespace VibeRe.Infrastructure.Projects;

public static class ProjectServiceCollectionExtensions
{
    /// <summary>Adds <see cref="ProjectActions"/> and offers each project's <c>vibe.re</c> actions as tools.</summary>
    public static IServiceCollection AddVibeReProjects(this IServiceCollection services)
    {
        services.AddSingleton<IProjectConfigLoader, ProjectConfigFileLoader>();
        services.AddSingleton<ICommandRunner, ShellCommandRunner>();
        services.AddSingleton<ProjectActions>();
        services.AddScoped<IToolSource, ProjectActionToolSource>();
        return services;
    }
}
