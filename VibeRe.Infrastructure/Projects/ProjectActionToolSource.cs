using Microsoft.Extensions.Logging;
using VibeRe.Application.Projects;
using VibeRe.Application.Tools;

namespace VibeRe.Infrastructure.Projects;

/// <summary>
/// Offers the actions in the working directory's <c>vibe.re</c> file as <c>project__{name}</c> tools. The file
/// is read on every listing, so edits apply to the next model turn. An invalid file is logged and contributes
/// no tools rather than hiding every other tool.
/// </summary>
public sealed class ProjectActionToolSource(
    ProjectActions actions,
    ILogger<ProjectActionToolSource>? logger = null) : IToolSource
{
    public async Task<IReadOnlyList<ITool>> GetToolsAsync(
        ToolContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var config = await actions.FindConfigAsync(context.WorkingDirectory, cancellationToken);
            return config is null
                ? []
                : config.Actions
                    .Where(action => action.Tool)
                    .Select(action => (ITool)new ProjectActionTool(config, action, actions))
                    .ToList();
        }
        catch (ProjectConfigException exception)
        {
            logger?.LogWarning(exception, "Skipping project actions: {Message}", exception.Message);
            return [];
        }
    }
}
