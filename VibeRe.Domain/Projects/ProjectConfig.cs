namespace VibeRe.Domain.Projects;

/// <summary>The contents of a project's <c>vibe.re</c> file, found at <see cref="RootDirectory"/>.</summary>
public sealed class ProjectConfig
{
    public const string FileName = "vibe.re";

    public ProjectConfig(string rootDirectory, IEnumerable<ProjectAction> actions)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        RootDirectory = rootDirectory;
        Actions = actions.ToList();

        var duplicate = Actions.GroupBy(action => action.Name).FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new ArgumentException($"More than one action is named '{duplicate.Key}'.", nameof(actions));
        }
    }

    /// <summary>The directory that holds the <c>vibe.re</c> file. Actions run here.</summary>
    public string RootDirectory { get; }

    public IReadOnlyList<ProjectAction> Actions { get; }
}
