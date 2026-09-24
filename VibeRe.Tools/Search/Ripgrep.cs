using System.ComponentModel;
using System.Diagnostics;

namespace VibeRe.Tools.Search;

/// <summary>Runs the <c>rg</c> executable: <c>$VIBE_RE_RG_PATH</c>, or <c>rg</c> on the <c>PATH</c>.</summary>
public sealed class Ripgrep(string? executable = null)
{
    public const string PathEnvironmentVariable = "VIBE_RE_RG_PATH";

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    /// <summary>Arguments every search uses: plain output, hidden files included, <c>.git</c> excluded.</summary>
    internal static readonly string[] CommonArguments =
        ["--color=never", "--hidden", "--glob=!.git/", "--sort=path", "--max-columns=500", "--max-columns-preview"];

    private readonly string _executable = executable
        ?? (Environment.GetEnvironmentVariable(PathEnvironmentVariable) is { Length: > 0 } configured ? configured : "rg");

    /// <summary>
    /// Runs ripgrep on <paramref name="searchPath"/> (relative to <paramref name="workingDirectory"/>, so output
    /// paths are too) and returns at most <paramref name="maxLines"/> lines, stopping the search early when there
    /// are more. "No matches" is an empty result, not an error.
    /// </summary>
    public async Task<RipgrepOutput> RunAsync(
        IEnumerable<string> arguments,
        string workingDirectory,
        string searchPath,
        int maxLines,
        CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(_executable)
        {
            WorkingDirectory = workingDirectory,
            // Without a path ripgrep would search a piped stdin, so always pass one and give it an empty stdin.
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in CommonArguments.Concat(arguments).Concat(["--", searchPath]))
        {
            start.ArgumentList.Add(argument);
        }

        using var process = StartProcess(start);
        process.StandardInput.Close();
        var prefix = searchPath == "." ? "./" : null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);
        var error = process.StandardError.ReadToEndAsync(timeout.Token);

        var lines = new List<string>();
        var truncated = false;
        try
        {
            while (await process.StandardOutput.ReadLineAsync(timeout.Token) is { } line)
            {
                if (lines.Count == maxLines)
                {
                    truncated = true;
                    break;
                }

                lines.Add(prefix is not null && line.StartsWith(prefix, StringComparison.Ordinal) ? line[prefix.Length..] : line);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ToolException($"The search took longer than {Timeout.TotalSeconds:0} seconds. Narrow it down.");
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }

        await process.WaitForExitAsync(CancellationToken.None);

        // Exit code 1 means "no matches"; 2 means an error, which ripgrep also reports when some files were
        // unreadable, so only fail when nothing useful came back.
        if (!truncated && process.ExitCode == 2 && lines.Count == 0)
        {
            throw new ToolException($"ripgrep failed: {(await error).Trim()}");
        }

        return new RipgrepOutput(lines, truncated);
    }

    private Process StartProcess(ProcessStartInfo start)
    {
        try
        {
            return Process.Start(start) ?? throw new Win32Exception();
        }
        catch (Win32Exception)
        {
            throw new ToolException(
                $"ripgrep ('{_executable}') could not be started. Install it (e.g. `brew install ripgrep`) " +
                $"or set {PathEnvironmentVariable}.");
        }
    }
}

public sealed record RipgrepOutput(IReadOnlyList<string> Lines, bool Truncated);
