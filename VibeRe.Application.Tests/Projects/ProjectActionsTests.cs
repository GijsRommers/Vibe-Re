using VibeRe.Application.Projects;
using VibeRe.Domain.Projects;

namespace VibeRe.Application.Tests.Projects;

public class ProjectActionsTests
{
    [Fact]
    public async Task Worktree_setup_runs_flagged_actions_in_order_and_stops_at_the_first_failure()
    {
        var config = new ProjectConfig("/repo/worktree",
        [
            new ProjectAction("install", "npm ci", runOnWorktreeCreate: true),
            new ProjectAction("build", "npm run build"),
            new ProjectAction("env", "cp $VIBE_RE_SOURCE_ROOT/.env .", runOnWorktreeCreate: true),
            new ProjectAction("migrate", "npm run migrate", runOnWorktreeCreate: true),
        ]);
        var runner = new RecordingRunner { ["cp $VIBE_RE_SOURCE_ROOT/.env ."] = 1 };
        var actions = new ProjectActions(new FixedLoader(config), runner);

        var runs = await actions.SetUpWorktreeAsync("/repo/worktree/src", "/repo/main");

        Assert.Equal(["install", "env"], runs.Select(run => run.Action.Name));
        Assert.False(runs[^1].Result.Succeeded);
        Assert.Equal(["npm ci", "cp $VIBE_RE_SOURCE_ROOT/.env ."], runner.Runs.Select(run => run.Command));
        Assert.All(runner.Runs, run =>
        {
            Assert.Equal("/repo/worktree", run.WorkingDirectory);
            Assert.Equal("/repo/worktree", run.Environment[ProjectActions.ProjectRootVariable]);
            Assert.Equal("/repo/main", run.Environment[ProjectActions.SourceRootVariable]);
        });
    }

    [Fact]
    public async Task Worktree_setup_does_nothing_without_a_vibe_re_file()
    {
        var runner = new RecordingRunner();

        Assert.Empty(await new ProjectActions(new FixedLoader(null), runner).SetUpWorktreeAsync("/wt", "/main"));
        Assert.Empty(runner.Runs);
    }

    private sealed class FixedLoader(ProjectConfig? config) : IProjectConfigLoader
    {
        public Task<ProjectConfig?> FindAsync(string directory, CancellationToken cancellationToken = default) =>
            Task.FromResult(config);
    }

    /// <summary>Exits 0 unless the command was given another exit code.</summary>
    private sealed class RecordingRunner : Dictionary<string, int>, ICommandRunner
    {
        public List<(string Command, string WorkingDirectory, IReadOnlyDictionary<string, string> Environment)> Runs { get; } = [];

        public Task<CommandResult> RunAsync(
            string command,
            string workingDirectory,
            IReadOnlyDictionary<string, string>? environment = null,
            CancellationToken cancellationToken = default)
        {
            Runs.Add((command, workingDirectory, environment ?? new Dictionary<string, string>()));
            return Task.FromResult(new CommandResult(TryGetValue(command, out var exitCode) ? exitCode : 0, $"ran {command}"));
        }
    }
}
