using System.Text.RegularExpressions;

namespace VibeRe.Domain.Projects;

/// <summary>
/// A named shell command from a project's <c>vibe.re</c> file, like <c>build</c> or <c>test</c>. Actions run from
/// the project root, can set up new worktrees and are offered to the model as <c>project__{Name}</c> tools.
/// </summary>
public sealed partial class ProjectAction
{
    public ProjectAction(
        string name,
        string command,
        string? description = null,
        bool runOnWorktreeCreate = false,
        bool tool = true)
    {
        if (!NamePattern().IsMatch(name) || name.Contains("__", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Action names are 1-32 lowercase letters, digits, '-' or '_', without '__'.", nameof(name));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        Name = name;
        Command = command;
        Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        RunOnWorktreeCreate = runOnWorktreeCreate;
        Tool = tool;
    }

    public string Name { get; }

    /// <summary>Run by the platform shell (<c>/bin/sh -c</c>, or <c>cmd /c</c> on Windows).</summary>
    public string Command { get; }

    public string? Description { get; }

    /// <summary>Runs in a new worktree right after it is created, e.g. to install dependencies.</summary>
    public bool RunOnWorktreeCreate { get; }

    /// <summary>
    /// Whether the model may call this action. Turn it off for commands that never exit, such as a dev server.
    /// </summary>
    public bool Tool { get; }

    [GeneratedRegex("^[a-z0-9][a-z0-9_-]{0,31}$")]
    private static partial Regex NamePattern();
}
