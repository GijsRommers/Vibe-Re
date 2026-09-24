namespace VibeRe.Application.Projects;

public interface ICommandRunner
{
    /// <summary>
    /// Runs <paramref name="command"/> in the platform shell and waits for it to exit. Cancelling kills the
    /// whole process tree.
    /// </summary>
    Task<CommandResult> RunAsync(
        string command,
        string workingDirectory,
        IReadOnlyDictionary<string, string>? environment = null,
        CancellationToken cancellationToken = default);
}

/// <summary>How a command exited, with stdout and stderr interleaved in the order they were written.</summary>
public sealed record CommandResult(int ExitCode, string Output)
{
    public bool Succeeded => ExitCode == 0;
}
