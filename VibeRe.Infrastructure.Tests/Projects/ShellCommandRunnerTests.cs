using System.Diagnostics;
using VibeRe.Infrastructure.Projects;

namespace VibeRe.Infrastructure.Tests.Projects;

public sealed class ShellCommandRunnerTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("vibe-re-tests").FullName;
    private readonly ShellCommandRunner _runner = new();

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public async Task Captures_stdout_stderr_and_the_exit_code()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // POSIX shell syntax.
        }

        var result = await _runner.RunAsync(
            "echo \"$GREETING from $(basename \"$PWD\")\"; echo oops >&2; exit 3",
            _dir,
            new Dictionary<string, string> { ["GREETING"] = "hello" });

        Assert.Equal(3, result.ExitCode);
        Assert.False(result.Succeeded);
        Assert.Contains($"hello from {Path.GetFileName(_dir)}", result.Output);
        Assert.Contains("oops", result.Output);
    }

    [Fact]
    public async Task Cancelling_kills_the_command()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        var stopwatch = Stopwatch.StartNew();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _runner.RunAsync("sleep 30", _dir, cancellationToken: cancellation.Token));

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10));
    }
}
