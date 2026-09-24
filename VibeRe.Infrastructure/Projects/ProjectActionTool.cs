using System.Text.Json;
using VibeRe.Application.Projects;
using VibeRe.Application.Tools;
using VibeRe.Domain.Projects;

namespace VibeRe.Infrastructure.Projects;

/// <summary>Runs one <see cref="ProjectAction"/> for the model. It takes no arguments: the command is fixed.</summary>
internal sealed class ProjectActionTool(ProjectConfig config, ProjectAction action, ProjectActions actions) : ITool
{
    /// <summary>Output kept for the model. The end is kept, since that is where errors and summaries are.</summary>
    internal const int MaxOutputLength = 30_000;

    private static readonly JsonElement NoArguments =
        JsonDocument.Parse("""{"type":"object","properties":{},"additionalProperties":false}""").RootElement;

    public string Name { get; } = $"project__{action.Name}";

    public string? Description { get; } =
        $"{action.Description ?? $"Runs the project's '{action.Name}' action."} Command: `{action.Command}`, run from the project root.";

    public JsonElement InputSchema => NoArguments;

    public async Task<ToolResult> InvokeAsync(
        JsonElement arguments,
        ToolContext context,
        CancellationToken cancellationToken = default)
    {
        var result = await actions.RunAsync(config, action, cancellationToken);
        var output = result.Output.Length > MaxOutputLength
            ? $"[{result.Output.Length - MaxOutputLength} characters of earlier output omitted]\n{result.Output[^MaxOutputLength..]}"
            : result.Output;
        return new ToolResult($"{output}\nExit code: {result.ExitCode}", IsError: !result.Succeeded);
    }
}
