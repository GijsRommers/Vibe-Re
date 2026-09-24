using VibeRe.Domain.Projects;

namespace VibeRe.Application.Projects;

public sealed record ActionRun(ProjectAction Action, CommandResult Result);

/// <summary>Runs the actions from a project's <c>vibe.re</c> file.</summary>
public sealed class ProjectActions(IProjectConfigLoader configs, ICommandRunner runner)
{
    /// <summary>Set for every action: the directory that holds the <c>vibe.re</c> file.</summary>
    public const string ProjectRootVariable = "VIBE_RE_PROJECT_ROOT";

    /// <summary>Set for worktree setup: the checkout the new worktree was created from.</summary>
    public const string SourceRootVariable = "VIBE_RE_SOURCE_ROOT";

    public Task<ProjectConfig?> FindConfigAsync(string directory, CancellationToken cancellationToken = default) =>
        configs.FindAsync(directory, cancellationToken);

    public Task<CommandResult> RunAsync(
        ProjectConfig config,
        ProjectAction action,
        CancellationToken cancellationToken = default) =>
        runner.RunAsync(
            action.Command,
            config.RootDirectory,
            new Dictionary<string, string> { [ProjectRootVariable] = config.RootDirectory },
            cancellationToken);

    /// <summary>
    /// Runs the <see cref="ProjectAction.RunOnWorktreeCreate"/> actions of a freshly created worktree, in file
    /// order, stopping at the first one that fails. The worktree's own <c>vibe.re</c> is used, so each branch
    /// sets itself up the way it expects. <paramref name="sourceRoot"/> is exposed as
    /// <c>VIBE_RE_SOURCE_ROOT</c>, e.g. for copying an untracked <c>.env</c> across.
    /// </summary>
    public async Task<IReadOnlyList<ActionRun>> SetUpWorktreeAsync(
        string worktreePath,
        string sourceRoot,
        CancellationToken cancellationToken = default)
    {
        var config = await configs.FindAsync(worktreePath, cancellationToken);
        if (config is null)
        {
            return [];
        }

        var environment = new Dictionary<string, string>
        {
            [ProjectRootVariable] = config.RootDirectory,
            [SourceRootVariable] = sourceRoot,
        };
        var runs = new List<ActionRun>();
        foreach (var action in config.Actions.Where(action => action.RunOnWorktreeCreate))
        {
            var result = await runner.RunAsync(action.Command, config.RootDirectory, environment, cancellationToken);
            runs.Add(new ActionRun(action, result));
            if (!result.Succeeded)
            {
                break;
            }
        }

        return runs;
    }
}
