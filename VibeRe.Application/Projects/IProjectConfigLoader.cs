using VibeRe.Domain.Projects;

namespace VibeRe.Application.Projects;

public interface IProjectConfigLoader
{
    /// <summary>
    /// Finds the <c>vibe.re</c> file that applies to <paramref name="directory"/>: the nearest one in it or a parent,
    /// without leaving the git repository or worktree it belongs to. Returns <c>null</c> when there is none.
    /// </summary>
    /// <exception cref="ProjectConfigException">The file exists but is invalid.</exception>
    Task<ProjectConfig?> FindAsync(string directory, CancellationToken cancellationToken = default);
}

public sealed class ProjectConfigException(string message, Exception? innerException = null)
    : Exception(message, innerException);
